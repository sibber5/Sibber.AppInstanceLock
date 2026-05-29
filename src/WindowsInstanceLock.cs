// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging;

namespace Sibber.AppInstanceLock;

[SupportedOSPlatform("windows")]
internal sealed class WindowsInstanceLock<TMessage> : InstanceLockImpl<TMessage>
{
    private readonly string _mutexName;

    private Mutex? _mutex;
    private bool _ownsMutex;

    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    /// <exception cref="InvalidOperationException">The current user's <see cref="SecurityIdentifier"/> could not be retrieved.</exception>
    /// <exception cref="System.Security.SecurityException">The caller does not have the required permissions to retrieve the current user identity.</exception>
    public WindowsInstanceLock(string appId, InstanceLockOptions options, ILogger<WindowsInstanceLock<TMessage>>? logger)
        : base(CreatePipeName(appId, options.Scope), options, logger)
    {
        _mutexName = CreateMutexName(appId, _options.Scope);
        _logger?.LogDebug(nameof(WindowsInstanceLock<>) + " initialized: mutex={Mutex} pipe={Pipe}", _mutexName, _pipeName);
    }

    /// <exception cref="NotSupportedException"><paramref name="scope"/> is not a supported scope.</exception>
    /// <inheritdoc cref="GetUserId"/>
    private static string CreatePipeName(string appId, InstanceLockScope scope) => scope switch
    { // Named pipe names on Windows are relative, e.g. \\.\pipe\<name>. Keep them simple.
        InstanceLockScope.Machine => $"si_{appId}",
        InstanceLockScope.User => $"si_{appId}_user_{GetUserId()}",
        InstanceLockScope.Session => $"si_{appId}_session_{GetSessionId()}",
        // _ => $"si_{baseName}",
        _ => throw new NotSupportedException(),
    };

    /// <exception cref="NotSupportedException"><paramref name="scope"/> is not a supported scope.</exception>
    /// <inheritdoc cref="GetUserId"/>
    private static string CreateMutexName(string appId, InstanceLockScope scope) => scope switch
    {
        InstanceLockScope.Machine => @$"Global\{appId}",
        InstanceLockScope.User => @$"Global\{appId}_user_{GetUserId()}",
        InstanceLockScope.Session => @$"Local\{appId}",
        _ => throw new NotSupportedException(),
    };

    /// <exception cref="IdentityNotMappedException">A <see cref="SecurityIdentifier"/> could not be mapped to a valid account.</exception>
    /// <exception cref="InvalidOperationException">The current user's <see cref="SecurityIdentifier"/> could not be retrieved.</exception>
    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    /// <exception cref="System.Security.SecurityException">The caller does not have the required permissions to retrieve the current user identity.</exception>
    private MutexSecurity CreateMutexSecurity()
    {
        var security = new MutexSecurity();
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(new(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), MutexRights.FullControl, AccessControlType.Allow));
        security.AddAccessRule(_options.Scope switch
        {
            InstanceLockScope.Machine => new(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null), MutexRights.Synchronize, AccessControlType.Allow),
            InstanceLockScope.User or InstanceLockScope.Session => new(WindowsIdentity.GetCurrent().User ?? throw new UnreachableException(), MutexRights.Synchronize, AccessControlType.Allow),
            _ => throw new NotSupportedException(),
        });
        return security;
    }

    /// <remarks></remarks>
    /// <note type="threadunsafe">This method is not thread-safe.</note>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    public override bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_isPrimary == true) throw new UnreachableException();

        bool createdNew;
        try
        {
            _mutex = MutexAcl.Create(initiallyOwned: true, _mutexName, out createdNew, CreateMutexSecurity());
            _ownsMutex = createdNew;
        }
        catch (Exception ex)
        {
            _logger?.LogDebug(ex, "Mutex creation failed; attempting OpenExisting.");
            Debug.Assert(!_ownsMutex);
            try
            {
                _mutex?.Dispose();
                _mutex = Mutex.OpenExisting(_mutexName);
                _ownsMutex = false;
                createdNew = false;
            }
            catch (Exception openEx)
            {
                _logger?.LogWarning(openEx, "OpenExisting failed; treating as non-primary.");
                _mutex = null;
                _ownsMutex = false;
                createdNew = false;
            }
        }

        if (createdNew)
        {
            _logger?.LogDebug("Mutex acquired (primary).");
            _isPrimary = true;
            return true;
        }

        _logger?.LogDebug("Mutex existed; not primary.");
        _isPrimary = false;
        return false;
    }

    /// <exception cref="IOException"></exception>
    protected override NamedPipeServerStream CreatePipeServer()
    {
        var options = PipeOptions.Asynchronous;
        // For user-scoped (and session-scoped, which falls under user), enforce current-user only to further reduce cross-user interference.
        if (_options.Scope is InstanceLockScope.User or InstanceLockScope.Session) options |= PipeOptions.CurrentUserOnly;

        // TODO: specify ACL to allow the minimum rights required.

#pragma warning disable Ex0100 // all of the exceptions that the constructor throws except for IOException are for invalid arguments. none of the arguments being passed in are invalid; _pipeName is sanitized.
        return new NamedPipeServerStream(
#pragma warning restore Ex0100
            _pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            options
        );
    }

    protected override void DisposeCore()
    {
        if (_mutex is not null)
        {
            if (_ownsMutex) try { _mutex.ReleaseMutex(); } catch { }
            _mutex.Dispose();
        }
    }

    /// <exception cref="InvalidOperationException">The <see cref="SecurityIdentifier"/> for the current user was <see langword="null"/>.</exception>
    /// <inheritdoc cref="WindowsIdentity.GetCurrent()" path="/exception"/>
    private static string GetUserId()
    {
        using var id = WindowsIdentity.GetCurrent();
        var sid = id.User?.Value ?? throw new InvalidOperationException("Could not get current user SDDL");
        return sid;
    }

    /// <inheritdoc cref="Process.SessionId" path="/exception"/>
    private static int GetSessionId() => Process.GetCurrentProcess().SessionId;
}
