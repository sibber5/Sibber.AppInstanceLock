// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using static Sibber.AppInstanceLock.PInvoke;

namespace Sibber.AppInstanceLock;

#if INCLUDE_TEST_HOOKS
internal static class UnixInstanceLockHooks
{
    internal static readonly AsyncLocal<Func<uint>?> _userIdHook = new();
}
#endif

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class UnixInstanceLock<TMessage> : InstanceLockImpl<TMessage>
{
    internal readonly string _lockFilePath;
    private readonly bool _isLockFileInSharedDir;
    private FileStream? _lockFileStream;

    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    /// <exception cref="SecurityException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="PlatformNotSupportedException">Getting the folder path of the user profile special folder is not supported on the current platform.</exception>
    public UnixInstanceLock(string appId, InstanceLockOptions options, ILogger<UnixInstanceLock<TMessage>>? logger)
        : base(CreatePipeName(appId, options.Scope), options, logger)
    {
        _lockFilePath = ChooseLockFilePath(appId, _options.Scope, logger, out _isLockFileInSharedDir);
        _logger?.LogDebug(nameof(UnixInstanceLock<>) + " initialized: lockFile={Lock} pipe={Pipe}", _lockFilePath, _pipeName);
    }

    /// <exception cref="NotSupportedException"><paramref name="scope"/> is not a supported scope.</exception>
    /// <inheritdoc cref="GetSessionId" path="/exception"/>
    // ExceptionAdjustment: M:System.Runtime.InteropServices.MemoryMarshal.AsBytes``1(System.ReadOnlySpan{``0}) -T:System.OverflowException
    private static string CreatePipeName(string appId, InstanceLockScope scope)
    {
        var rawName = scope switch
        {
            InstanceLockScope.Machine => $"si_{appId}",
            InstanceLockScope.User => $"si_{appId}_user_{getuid()}",
            InstanceLockScope.Session => $"si_{appId}_session_{GetSessionId()}",
            _ => throw new NotSupportedException($"{scope} is not a supported scope."),
        };

        // macOS has a 104-character limit for Unix domain socket paths.
        // The pipe is created at Path.GetTempPath() + "CoreFxPipe_" + pipeName.
        // If the pipe name is too long, we hash it to fit safely.
        if (rawName.Length <= 32) return rawName;

        var rawNameBytes = MemoryMarshal.AsBytes(rawName.AsSpan());

        Span<byte> hash = stackalloc byte[32]; // SHA256 is 32 bytes
        System.Security.Cryptography.SHA256.HashData(rawNameBytes, hash);

        // Limit to 16 bytes (32 hex characters) to ensure the final socket path length
        // stays well below the macOS 104-character limit. .NET prepends Path.GetTempPath()
        // (which can be ~50 chars on macOS) and "CoreFxPipe_".
        Span<char> hexString = stackalloc char[32];
        for (var i = 0; i < 16; i++)
        {
            _ = hash[i].TryFormat(hexString[(i * 2)..], out _, "x2", CultureInfo.InvariantCulture);
        }

        return string.Concat("si_", hexString);
    }

    /// <exception cref="SecurityException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="PlatformNotSupportedException">Getting the folder path of the user profile special folder is not supported on the current platform.</exception>
    private static string ChooseLockFilePath(string appId, InstanceLockScope scope, ILogger? logger, out bool isLockFileInSharedDir)
    {
        isLockFileInSharedDir = false;
        // Session scope: use XDG_RUNTIME_DIR or /run/user/{uid}; fall back to /tmp with session id.
        if (scope is InstanceLockScope.Session)
        {
            var xdg = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (!string.IsNullOrEmpty(xdg))
            {
                try
                {
                    Directory.CreateDirectory(xdg);
                    return Path.Combine(xdg, $"{appId}.lock");
                }
                catch (Exception ex)
                {
                    logger?.LogDebug(ex, "Failed to create lock directory at XDG_RUNTIME_DIR '{Path}'. Falling back...", xdg);
                }
            }

            try
            {
                var uid = getuid();
                var runUser = $"/run/user/{uid}";
                if (Directory.Exists(runUser))
                {
                    return Path.Combine(runUser, $"{appId}.lock");
                }
            }
            catch (Exception ex)
            {
                logger?.LogDebug(ex, "Failed to evaluate /run/user/ directory. Falling back...");
            }

            // fallback: /tmp with session id suffix
            isLockFileInSharedDir = true;
            return Path.Combine(Path.GetTempPath(), $"{appId}_session_{GetSessionId()}.lock");
        }
        // User scope: place lockfile in a location inside the user's home (shared across sessions).
        else if (scope is InstanceLockScope.User)
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrEmpty(home))
            {
                // Prefer ~/.local/share/<app> (common on Linux), or macOS Application Support.
                if (OperatingSystem.IsMacOS())
                {
                    var macPath = Path.Combine(home, "Library", "Application Support", appId);
                    try
                    {
                        Directory.CreateDirectory(macPath);
                        return Path.Combine(macPath, $"{appId}.lock");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to create lock directory at '{Path}'. Falling back...", macPath);
                    }
                }
                else if (OperatingSystem.IsLinux())
                {
                    // Linux-ish: use ~/.local/share/<app>
                    var localShare = Path.Combine(home, ".local", "share", appId);
                    try
                    {
                        Directory.CreateDirectory(localShare);
                        return Path.Combine(localShare, $"{appId}.lock");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to create lock directory at '{Path}'. Falling back...", localShare);
                    }

                    // fallback to ~/.config/<app>
                    var config = Path.Combine(home, ".config", appId);
                    try
                    {
                        Directory.CreateDirectory(config);
                        return Path.Combine(config, $"{appId}.lock");
                    }
                    catch (Exception ex)
                    {
                        logger?.LogDebug(ex, "Failed to create lock directory at '{Path}'. Falling back...", config);
                    }
                }
                else
                {
                    throw new UnreachableException($"Unexpected platform ({Environment.OSVersion.Platform}) in {nameof(ChooseLockFilePath)}.");
                }
            }

            // If we couldn't use home, fallback to /tmp with UID prefix (less ideal).
            isLockFileInSharedDir = true;
            return Path.Combine(Path.GetTempPath(), $"{appId}_user_{getuid()}.lock");
        }
        // Machine scope: try a standard system lock dir, else /tmp with 'machine' prefix.
        else if (scope is InstanceLockScope.Machine)
        {
            try
            {
                const string SystemLockDir = "/var/lock";
                if (Directory.Exists(SystemLockDir))
                {
                    return Path.Combine(SystemLockDir, $"{appId}.lock");
                }
            }
            catch
            {
                /* ignore */
            }

            // fallback to /tmp
            isLockFileInSharedDir = true;
            return Path.Combine(Path.GetTempPath(), $"machine_{appId}.lock");
        }

        throw new UnreachableException($"Unexpected scope ({scope}) in {nameof(ChooseLockFilePath)}.");
    }

    /// <remarks></remarks>
    /// <note type="threadunsafe">This method is not thread-safe.</note>
    /// <exception cref="ObjectDisposedException"></exception>
    /// <exception cref="UnauthorizedAccessException">Failed to create the lock file because another user has already created a restrictive file at the path (lock file squatting).</exception>
    public override bool TryAcquirePrimary()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_isPrimary == true) throw new UnreachableException();

        try
        {
            // Ensure the parent directory exists (for user scope we already did but be safe)
            var parent = Path.GetDirectoryName(_lockFilePath);
            if (!string.IsNullOrEmpty(parent))
            {
                try { Directory.CreateDirectory(parent); } catch { /* ignore */ }
            }

            var unixCreateMode = _options.Scope is InstanceLockScope.Machine
                ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite
                : UnixFileMode.UserRead | UnixFileMode.UserWrite;

            var fsOptions = new FileStreamOptions
            {
                Mode = FileMode.OpenOrCreate,
                Access = FileAccess.ReadWrite,
                Share = FileShare.ReadWrite,
                UnixCreateMode = unixCreateMode,
            };
            var fs = new FileStream(_lockFilePath, fsOptions);
            _lockFileStream = fs;

            // get native file descriptor and attempt flock non-blocking exclusive
            int res;
            var addRefSuccess = false;
            try
            {
                fs.SafeFileHandle.DangerousAddRef(ref addRefSuccess);
                var fd = fs.SafeFileHandle.DangerousGetHandle().ToInt32();
                res = flock(fd, LOCK_EX | LOCK_NB);
            }
            finally
            {
                if (addRefSuccess) fs.SafeFileHandle.DangerousRelease();
            }

            if (res is 0)
            {
                _logger?.LogDebug("Acquired flock on {Path}; primary.", _lockFilePath);
                _isPrimary = true;
                return true;
            }

            if (res is not -1)
            {
                throw new UnreachableException($"flock returned unexpected value: {res}.");
            }

            // did not acquire (not primary instance) - another process holds the lock
            var err = Marshal.GetLastPInvokeError();
            _logger?.LogDebug("flock failed with errno={Error}; not primary.", err);
            _lockFileStream.Dispose();
            _lockFileStream = null;
            _isPrimary = false;
            return false;
        }
        catch (UnauthorizedAccessException ex) when (_isLockFileInSharedDir)
        {
            _logger?.LogError(ex, "UnauthorizedAccessException when opening lock file {Path} in a shared temporary directory.", _lockFilePath);
            throw;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to open/lock {Path}; will act as non-primary.", _lockFilePath);
            _lockFileStream?.Dispose();
            _lockFileStream = null;
            _isPrimary = false;
            return false;
        }
    }

    /// <exception cref="IOException"></exception>
    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    protected override NamedPipeServerStream CreatePipeServer()
    {
        var options = PipeOptions.Asynchronous;

        if (_options.Scope is InstanceLockScope.User or InstanceLockScope.Session)
        {
            options |= PipeOptions.CurrentUserOnly;
        }
        else if (_options.Scope is not InstanceLockScope.Machine)
        {
            throw new NotSupportedException($"{_options.Scope} is not a supported scope.");
        }

#pragma warning disable Ex0100 // all of the exceptions that the constructor throws except for IOException are for invalid arguments. none of the arguments being passed in are invalid; _pipeName is sanitized.
        return new(
#pragma warning restore Ex0100
            _pipeName,
            PipeDirection.In,
            maxNumberOfServerInstances: 1,
            PipeTransmissionMode.Byte,
            options
        );
    }

    protected override void DisposeCore() => _lockFileStream?.Dispose();

    /// <exception cref="InvalidOperationException">
    /// If on Linux: The environment variable <c>XDG_SESSION_ID</c> is not set (e.g., not a systemd-logind session).<br/>
    /// If on macOS: The Security Session ID could not be retrieved.
    /// </exception>
    /// <exception cref="SecurityException">The caller is not authorized to retrieve the session ID.</exception>
    private static string GetSessionId()
    {
        // Get the Login Session Identifier (Stable, Session-Specific)
        if (OperatingSystem.IsLinux())
        {
            var sessionId = Environment.GetEnvironmentVariable("XDG_SESSION_ID");

            if (!string.IsNullOrEmpty(sessionId))
            {
                return sessionId;
            }

            // Fallback 1: Systemd Native API
            try
            {
                unsafe
                {
                    nint sessionPtr = 0;
                    var res = sd_pid_get_session(0, &sessionPtr);
                    if (res >= 0)
                    {
#pragma warning disable CA1508 // Avoid dead conditional code (Pointer is mutated by unmanaged code)
                        if (sessionPtr != 0)
#pragma warning restore CA1508
                        {
                            var sysSession = Marshal.PtrToStringUTF8(sessionPtr);
                            free(sessionPtr);
                            if (!string.IsNullOrEmpty(sysSession))
                            {
                                return sysSession;
                            }
                        }
                    }
                    // Negative errno return value. e.g. -ENODATA (-61) meaning process not part of a login session.
                    // Other errors: -ESRCH, -EINVAL, -ENOMEM.
                    // We safely fall through to Fallback 2.
                }
            }
            catch (DllNotFoundException)
            {
                // Fallback to Fallback 2
            }
            catch (EntryPointNotFoundException)
            {
                // Fallback to Fallback 2
            }

            // Fallback 2: Kernel Audit Subsystem
            const string AuditFilePath = "/proc/self/sessionid";
            if (File.Exists(AuditFilePath))
            {
                try
                {
                    var content = File.ReadAllText(AuditFilePath).Trim();
                    if (content != "4294967295" && !string.IsNullOrEmpty(content))
                    {
                        return content;
                    }
                }
                catch
                {
                    // Ignore and fall through to throw below
                }
            }

            throw new InvalidOperationException("Could not determine login session. The XDG_SESSION_ID environment variable is not set, sd_pid_get_session failed, and the kernel audit subsystem is unavailable or disabled.");
        }

        if (OperatingSystem.IsMacOS())
        {
            // Call SessionGetInfo, passing the 'callerSecuritySession' constant
            // as the input, and getting the actual session ID as the output.
            //
            // We pass null (0) for the attributes because we don't need them.
            //
            // We use 'Security.callerSecuritySession' which loads the constant's value.
            var osStatus = SessionGetInfo(CallerSecuritySession, out var sessionId, 0); // We don't need the attributes

            if (osStatus is ErrSessionSuccess)
            {
                Debug.Assert(sessionId is not uint.MaxValue and not 0);
                return sessionId.ToString(CultureInfo.InvariantCulture);
            }

            if (osStatus is ErrSessionAuthorizationDenied)
            {
                throw new SecurityException($"Caller is not authorized to retrieve the current session info. (OSStatus: {ErrSessionAuthorizationDenied})");
            }

            throw new InvalidOperationException($"Failed to retrieve macOS security session. SessionGetInfo returned OSStatus {osStatus}.");
        }

        throw new UnreachableException($"Unexpected platform ({Environment.OSVersion.Platform}) in {nameof(GetSessionId)}().");
    }

#if INCLUDE_TEST_HOOKS
#pragma warning disable IDE1006 // Naming styles
    private static uint getuid() => UnixInstanceLockHooks._userIdHook.Value?.Invoke() ?? PInvoke.getuid();
#pragma warning restore IDE1006
#endif
}

[SuppressMessage("Security", "CA5392:Use DefaultDllImportSearchPaths attribute for P/Invokes", Justification = "These are Unix APIs.")]
internal static partial class PInvoke
{
    // P/Invoke flock flags
    // public const int LOCK_SH = 1; // shared
    public const int LOCK_EX = 2; // exclusive
    public const int LOCK_NB = 4; // non-blocking
    // public const int LOCK_UN = 8; // unlock

    public const uint CallerSecuritySession = uint.MaxValue;

    public const int ErrSessionSuccess = 0;
    // public const int ErrSessionInvalidId = -60500;
    // public const int ErrSessionInvalidAttributes = -60501;
    public const int ErrSessionAuthorizationDenied = -60502;

    [LibraryImport("libc", SetLastError = true)]
    public static partial int flock(int fd, int operation);

    [LibraryImport("libc", SetLastError = false)]
    public static partial uint getuid();

    [LibraryImport("libc", SetLastError = false)]
    public static partial void free(nint ptr);

    [LibraryImport("libsystemd.so.0", SetLastError = false)]
    public static unsafe partial int sd_pid_get_session(int pid, nint* session);

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security", SetLastError = false)]
    public static partial int SessionGetInfo(uint session, out uint sessionId, nint pAttributes); // pAttributes is nullable.
}
