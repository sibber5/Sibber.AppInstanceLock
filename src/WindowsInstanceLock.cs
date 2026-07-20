// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipes;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Serialization.Metadata;
using Microsoft.Extensions.Logging;

namespace Sibber.AppInstanceLock;

#if INCLUDE_TEST_HOOKS
internal static class WindowsInstanceLockHooks
{
    internal static readonly AsyncLocal<Func<string>?> _userIdHook = new();
    internal static readonly AsyncLocal<Func<int>?> _sessionIdHook = new();
}
#endif

[SupportedOSPlatform("windows")]
internal sealed class WindowsInstanceLock<TMessage> : InstanceLockImpl<TMessage>
{
    internal readonly string _mutexName;

    private Mutex? _mutex;
    private bool _ownsMutex;
    private Thread? _mutexThread;
    private readonly ManualResetEventSlim _disposeEvent = new(false);

    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    /// <exception cref="InvalidOperationException">The current user's <see cref="SecurityIdentifier"/> could not be retrieved.</exception>
    /// <exception cref="System.Security.SecurityException">The caller does not have the required permissions to retrieve the current user identity.</exception>
    public WindowsInstanceLock(string appId, InstanceLockOptions options, ILogger<WindowsInstanceLock<TMessage>>? logger, JsonTypeInfo<TMessage>? jsonTypeInfo)
        : base(CreatePipeName(appId, options.Scope), options, logger, jsonTypeInfo)
    {
        _mutexName = CreateMutexName(appId, _options.Scope);
        _logger?.LogDebug(nameof(WindowsInstanceLock<>) + " initialized: mutex={Mutex} pipe={Pipe}", _mutexName, _pipeName);
    }

    /// <exception cref="NotSupportedException"><paramref name="scope"/> is not a supported scope.</exception>
    /// <inheritdoc cref="GetUserId"/>
    internal static string CreatePipeName(string appId, InstanceLockScope scope) => scope switch
    { // Named pipe names on Windows are relative, e.g. \\.\pipe\<name>. Keep them simple.
        InstanceLockScope.Machine => $"si_{appId}",
        InstanceLockScope.User => $"si_{appId}_user_{HashIfTooLong(GetUserId())}",
        InstanceLockScope.Session => $"si_{appId}_session_{GetSessionId()}",
        // _ => $"si_{baseName}",
        _ => throw new NotSupportedException($"{scope} is not a supported scope."),
    };

    /// <exception cref="NotSupportedException"><paramref name="scope"/> is not a supported scope.</exception>
    /// <inheritdoc cref="GetUserId"/>
    internal static string CreateMutexName(string appId, InstanceLockScope scope) => scope switch
    {
        InstanceLockScope.Machine => @$"Global\{appId}",
        InstanceLockScope.User => @$"Global\{appId}_user_{HashIfTooLong(GetUserId())}",
        InstanceLockScope.Session => @$"Local\{appId}_session_{GetSessionId()}",
        _ => throw new NotSupportedException($"{scope} is not a supported scope."),
    };

    // ExceptionAdjustment: M:System.Runtime.InteropServices.MemoryMarshal.AsBytes``1(System.ReadOnlySpan{``0}) -T:System.OverflowException
    private static string HashIfTooLong(string sid)
    {
        if (sid.Length <= 64) return sid;

        var sidBytes = MemoryMarshal.AsBytes(sid.AsSpan());
        Span<byte> hash = stackalloc byte[32]; // SHA256 is 32 bytes
        System.Security.Cryptography.SHA256.HashData(sidBytes, hash);
        return Convert.ToHexStringLower(hash);
    }

    /// <exception cref="IdentityNotMappedException">A <see cref="SecurityIdentifier"/> could not be mapped to a valid account.</exception>
    /// <exception cref="InvalidOperationException">The current user's <see cref="SecurityIdentifier"/> could not be retrieved.</exception>
    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    /// <exception cref="System.Security.SecurityException">The caller does not have the required permissions to retrieve the current user identity.</exception>
    internal MutexSecurity CreateMutexSecurity()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));

        // Grant the current user FullControl so MutexAcl.Create can successfully return a handle
        security.AddAccessRule(new(new SecurityIdentifier(GetUserId()), MutexRights.FullControl, AccessControlType.Allow));

        if (_options.Scope is InstanceLockScope.Machine)
        {
            security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
        }
        else if (_options.Scope is not (InstanceLockScope.User or InstanceLockScope.Session))
        {
            throw new NotSupportedException($"{_options.Scope} is not a supported scope.");
        }

        return security;
    }

    /// <remarks></remarks>
    /// <note type="threadunsafe">This method is not thread-safe.</note>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
    public override bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_isPrimary == true) throw new UnreachableException();

        using var attemptComplete = new ManualResetEventSlim(false);
        var success = false;
        ExceptionDispatchInfo? threadExceptionInfo = null;

        var t = new Thread(() =>
        {
            var attemptCompleteSignaled = false;
            try
            {
                try
                {
                    _mutex = MutexAcl.Create(initiallyOwned: false, _mutexName, out _, CreateMutexSecurity());
                }
                catch (Exception ex)
                {
                    _logger?.LogDebug(ex, "Mutex creation failed; attempting OpenExisting.");
                    try
                    {
                        _mutex = Mutex.OpenExisting(_mutexName);
                    }
                    catch (UnauthorizedAccessException openUae)
                    {
                        _logger?.LogError(openUae, "UnauthorizedAccessException when opening mutex {Mutex}.", _mutexName);
                        threadExceptionInfo = ExceptionDispatchInfo.Capture(openUae);
                        return;
                    }
                    catch (Exception openEx)
                    {
                        _logger?.LogWarning(openEx, "OpenExisting failed; treating as non-primary.");
                        return;
                    }
                }

                try
                {
                    _ownsMutex = _mutex.WaitOne(0);
                }
                catch (AbandonedMutexException)
                {
                    _ownsMutex = true;
                }

                if (_ownsMutex)
                {
                    success = true;

                    attemptCompleteSignaled = true;
                    attemptComplete.Set();

                    try
                    {
                        _disposeEvent.Wait();
                    }
                    catch (ObjectDisposedException) { }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Mutex dispose event Wait() unexpectedly threw and exception. Releasing mutex...");
                    }

                    try
                    {
                        _mutex.ReleaseMutex();
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Mutex release failed.");
                    }

                    try
                    {
                        _mutex.Dispose();
                    }
                    catch { }
                }
                else
                {
                    try
                    {
                        _mutex.Dispose();
                    }
                    catch { }

                    _mutex = null;
                }
            }
            catch (Exception ex)
            {
                threadExceptionInfo = ExceptionDispatchInfo.Capture(ex);
            }
            finally
            {
                if (!attemptCompleteSignaled)
                {
                    try { attemptComplete.Set(); } catch (ObjectDisposedException) { }
                }
            }
        })
        {
            IsBackground = true,
            Name = $"Sibber.AppInstanceLock Mutex Thread - {_mutexName}",
        };

        t.Start();

        attemptComplete.Wait();

        threadExceptionInfo?.Throw();

        if (success)
        {
            _mutexThread = t;
            _logger?.LogDebug("Mutex acquired (primary).");
            _isPrimary = true;
            return true;
        }

        _logger?.LogDebug("Mutex existed; not primary.");
        _isPrimary = false;
        return false;
    }

    /// <exception cref="IOException"></exception>
    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    /// <inheritdoc cref="GetUserId"/>
    protected override NamedPipeServerStream CreatePipeServer()
    {
        var options = PipeOptions.Asynchronous;

        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));

        var usersRights = PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize;

        if (_options.Scope is InstanceLockScope.Machine)
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), usersRights, AccessControlType.Allow));
        }
        else if (_options.Scope is InstanceLockScope.User or InstanceLockScope.Session)
        {
            security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(GetUserId()), usersRights, AccessControlType.Allow));
        }
        else
        {
            throw new NotSupportedException($"{_options.Scope} is not a supported scope.");
        }

#pragma warning disable Ex0100 // all of the exceptions that the constructor throws except for IOException are for invalid arguments. none of the arguments being passed in are invalid; _pipeName is sanitized.
        return NamedPipeServerStreamAcl.Create(
#pragma warning restore Ex0100
            _pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            options,
            inBufferSize: 0,
            outBufferSize: 0,
            security
        );
    }

    protected override void DisposeCore()
    {
        _disposeEvent.Set();
        try { _mutexThread?.Join(); } catch { }
        _disposeEvent.Dispose();
    }

    /// <exception cref="InvalidOperationException">The <see cref="SecurityIdentifier"/> for the current user was <see langword="null"/>.</exception>
    /// <inheritdoc cref="WindowsIdentity.GetCurrent()" path="/exception"/>
    private static string GetUserId()
    {
#if INCLUDE_TEST_HOOKS
        if (WindowsInstanceLockHooks._userIdHook.Value is { } hook) return hook();
#endif
        using var id = WindowsIdentity.GetCurrent();
        var sid = id.User?.Value ?? throw new InvalidOperationException("Could not get current user SDDL");
        return sid;
    }

    /// <inheritdoc cref="Process.SessionId" path="/exception"/>
    private static int GetSessionId()
    {
#if INCLUDE_TEST_HOOKS
        if (WindowsInstanceLockHooks._sessionIdHook.Value is { } hook) return hook();
#endif
        using var proc = Process.GetCurrentProcess();
        return proc.SessionId;
    }
}
