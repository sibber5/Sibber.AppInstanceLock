// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using static Sibber.AppInstanceLock.PInvoke;

namespace Sibber.AppInstanceLock;

[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
internal sealed class UnixInstanceLock<TMessage> : InstanceLockImpl<TMessage>
{
    private readonly string _lockFilePath;

    private FileStream? _lockFileStream;
    private bool _ownsLock;

    /// <exception cref="SecurityException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="PlatformNotSupportedException">Getting the folder path of the user profile special folder is not supported on the current platform.</exception>
    public UnixInstanceLock(string appId, InstanceLockOptions options, ILogger<UnixInstanceLock<TMessage>>? logger)
        : base(CreatePipeName(appId, options.Scope), options, logger)
    {
        _lockFilePath = ChooseLockFilePath(appId, _options.Scope);
        _logger?.LogDebug(nameof(UnixInstanceLock<>) + " initialized: lockFile={Lock} pipe={Pipe}", _lockFilePath, _pipeName);
    }

    /// <inheritdoc cref="GetSessionId" path="/exception"/>
    private static string CreatePipeName(string appId, InstanceLockScope scope) => scope switch
    {
        InstanceLockScope.Machine => $"si_{appId}",
        InstanceLockScope.User => $"si_{appId}_user_{getuid()}",
        InstanceLockScope.Session => $"si_{appId}_session_{GetSessionId()}",
        _ => $"si_{appId}",
    };

    /// <exception cref="SecurityException"></exception>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="PlatformNotSupportedException">Getting the folder path of the user profile special folder is not supported on the current platform.</exception>
    private static string ChooseLockFilePath(string appId, InstanceLockScope scope)
    {
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
                catch { /* fallthrough */ }
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
            catch { /* fallthrough */ }

            // fallback: /tmp with session id suffix
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
                    catch { /* fallthrough */ }
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
                    catch { /* fallthrough */ }

                    // fallback to ~/.config/<app>
                    var config = Path.Combine(home, ".config", appId);
                    try
                    {
                        Directory.CreateDirectory(config);
                        return Path.Combine(config, $"{appId}.lock");
                    }
                    catch { /* fallthrough */ }
                }
                else
                {
                    throw new UnreachableException($"Unexpected platform ({Environment.OSVersion.Platform}) in {nameof(ChooseLockFilePath)}.");
                }
            }

            // If we couldn't use home, fallback to /tmp with UID prefix (less ideal).
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
            return Path.Combine(Path.GetTempPath(), $"machine_{appId}.lock");
        }

        throw new UnreachableException($"Unexpected scope ({scope}) in {nameof(ChooseLockFilePath)}.");
    }

    /// <remarks></remarks>
    /// <note type="threadunsafe">This method is not thread-safe.</note>
    /// <exception cref="ObjectDisposedException"></exception>
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

            // open or create the lock file
            var fs = new FileStream(_lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);
            _lockFileStream = fs;

            // get native file descriptor and attempt flock non-blocking exclusive
            var fd = fs.SafeFileHandle.DangerousGetHandle().ToInt32();
            var res = flock(fd, LOCK_EX | LOCK_NB);
            if (res is 0)
            {
                _ownsLock = true;
                _logger?.LogDebug("Acquired flock on {Path}; primary.", _lockFilePath);
                _isPrimary = true;
                return true;
            }

            // did not acquire (not primary instance) - another process holds the lock
            var err = Marshal.GetLastPInvokeError();
            _logger?.LogDebug("flock failed with errno={Error}; not primary.", err);
            // close our stream — we do not own the lock
            _lockFileStream.Dispose();
            _lockFileStream = null;
            _ownsLock = false;
            _isPrimary = false;
            return false;
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Failed to open/lock {Path}; will act as non-primary.", _lockFilePath);
            _lockFileStream?.Dispose();
            _lockFileStream = null;
            _ownsLock = false;
            _isPrimary = false;
            return false;
        }
    }

    /// <exception cref="IOException"></exception>
#pragma warning disable Ex0100 // all of the exceptions that the constructor throws except for IOException are for invalid arguments. none of the arguments being passed in are invalid; _pipeName is sanitized.
    protected override NamedPipeServerStream CreatePipeServer() => new
#pragma warning restore Ex0100
    (
        _pipeName,
        PipeDirection.In,
        maxNumberOfServerInstances: 1,
        PipeTransmissionMode.Byte,
        PipeOptions.Asynchronous
    );

    protected override void DisposeCore()
    {
        _lockFileStream?.Dispose();

        if (_ownsLock)
        {
            // best-effort removal of lockfile; do not throw on error
            try { File.Delete(_lockFilePath); }
            catch (Exception ex) { _logger?.LogDebug(ex, "Failed to delete lockfile {Path} on dispose (non-fatal).", _lockFilePath); }
        }
    }

    /// <exception cref="InvalidOperationException">
    /// If on Linux: The environment variable <c>XDG_SESSION_ID</c> is not set (e.g., not a systemd-logind session).<br/>
    /// If on macOS: The Security Session ID could not be retrieved.
    /// </exception>
    /// <exception cref="SecurityException">The caller is not authorized to retrieve the session ID.</exception>
    private static string GetSessionId()
    {
        // Get the Logon Session Identifier (Stable, Session-Specific)
        if (OperatingSystem.IsLinux())
        {
            // Per requirement, we ONLY check for systemd-logind's variable.
            var sessionId = Environment.GetEnvironmentVariable("XDG_SESSION_ID");

            if (string.IsNullOrEmpty(sessionId))
            {
                throw new InvalidOperationException("Could not determine systemd logon session. The XDG_SESSION_ID environment variable is not set.");
            }

            return sessionId;
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
}

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

#pragma warning disable CA5392 // Use DefaultDLLImportSearchPaths attribute for P/Invokes.

    [LibraryImport("libc", SetLastError = true)]
    public static partial int flock(int fd, int operation);

    [LibraryImport("libc", SetLastError = false)]
    public static partial uint getuid();

    [LibraryImport("/System/Library/Frameworks/Security.framework/Security", SetLastError = false)]
    public static partial int SessionGetInfo(uint session, out uint sessionId, nint pAttributes); // pAttributes is nullable.

#pragma warning restore CA5392
}
