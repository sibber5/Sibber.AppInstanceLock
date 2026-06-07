// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;

namespace Sibber.AppInstanceLock;

/// <summary>
/// Manages the acquisition of an application instance lock to ensure that only a single instance
/// of the application is running at a time within a given scope. The instance that successfully
/// acquires the lock is referred to as the primary instance.<br/>
/// Optionally, allows secondary instances to send a message to the primary instance on startup.
/// </summary>
/// <note type="important">Acquiring the instance lock must be done on app startup as soon as possible, and only once.</note>
/// <typeparam name="TMessage">
/// The type of the message used for inter-instance notifications when another instance of the application is attempted to be opened.
/// </typeparam>
[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public sealed class InstanceLock<TMessage> : IDisposable
{
    private readonly string _appId;
    private readonly InstanceLockOptions _options;
    private readonly ILogger<InstanceLock<TMessage>>? _logger;
    internal readonly InstanceLockImpl<TMessage> _backend;

    private readonly Func<TMessage>? _createMsgToPrimary;
    private readonly Func<TMessage, ValueTask>? _onOtherInstanceOpened;
    private readonly Func<Exception, bool>? _onServerException;

    internal Task? _pipeServerLoopTask;

    /// <param name="appId">
    /// <para>
    /// A globally unique identifier for the application (e.g., a package ID, a GUID, or a combination).
    /// </para>
    /// <para>
    /// Must consist of ASCII alphanumeric characters, '-', and '_'.
    /// </para>
    /// </param>
    /// <param name="createMsgToPrimary">
    /// A factory delegate invoked to create the message that will be sent to the primary instance
    /// when this instance fails to acquire the lock (i.e., this is a secondary instance).
    /// The factory is called once per <see cref="TryAcquireOrNotify"/> invocation that fails to acquire.
    /// <br/>See <see cref="TryAcquireOrNotify"/> for more details.
    /// </param>
    /// <param name="onOtherInstanceOpened">
    /// <para>
    /// A callback that is invoked in the primary instance when another instance is attempted to be opened.<br/>
    /// See summary of <see cref="TryAcquireOrNotify"/> for more details.
    /// </para>
    /// <para>
    /// Exceptions thrown by this callback are caught, logged, and swallowed to keep the IPC server alive.
    /// If you need to handle exceptions programmatically, use a <see langword="try"/>/<see langword="catch"/> block inside the callback.
    /// </para>
    /// </param>
    /// <param name="onServerException">
    /// Invoked when an exception is thrown by the IPC server that listens for notifications from other instances that try to acquire the lock.
    /// If the return value is <see langword="false"/> the server will terminate, otherwise it will be restarted according to the <see cref="InstanceServerRetryPolicy"/>.
    /// <br/>
    /// See remarks for <see cref="TryAcquireOrNotify"/> for more details.
    /// </param>
    /// <param name="loggerFactory">An optional logger factory if logging by the <see cref="InstanceLock{TMessage}"/> is desired.</param>
    /// <param name="options">The configuration options.</param>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="appId"/> is <see langword="null"/>.<br/>
    /// - OR -<br/>
    /// One of the parameters <paramref name="createMsgToPrimary"/> and <paramref name="onOtherInstanceOpened"/> is <see langword="null"/> but the other one is not.
    /// </exception>
    /// <exception cref="ArgumentException"><paramref name="appId"/> is empty, or contains invalid characters.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="appId"/> is longer than 128 characters.</exception>
    /// <exception cref="NotSupportedException">The current operating system is not supported. Only Windows, Linux, and macOS are supported.</exception>
    /// <exception cref="System.Security.SecurityException">The caller does not have the required permissions to determine the session or user identity on the current platform.</exception>
    // ExceptionAdjustment: M:System.Guid.ToString(System.String) -T:System.FormatException
    public InstanceLock(string appId, Func<TMessage>? createMsgToPrimary = null, Func<TMessage, ValueTask>? onOtherInstanceOpened = null, Func<Exception, bool>? onServerException = null, ILoggerFactory? loggerFactory = null, InstanceLockOptions? options = null)
    {
        if (onOtherInstanceOpened is null && createMsgToPrimary is not null) throw new ArgumentNullException(nameof(onOtherInstanceOpened), $"{nameof(onOtherInstanceOpened)} is null, but {nameof(createMsgToPrimary)} is not null.");
        if (createMsgToPrimary is null && onOtherInstanceOpened is not null) throw new ArgumentNullException(nameof(createMsgToPrimary), $"{nameof(createMsgToPrimary)} is null, but {nameof(onOtherInstanceOpened)} is not null.");
        ArgumentNullException.ThrowIfNull(appId);
        if (appId.Length > 128) throw new ArgumentOutOfRangeException(nameof(appId), "App ID must be 128 characters or less.");
        foreach (var c in appId.AsSpan())
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))
            {
                throw new ArgumentException($"Invalid app ID value: '{appId}'. Unique {nameof(appId)} consisting of alphanumeric characters, '-', and '_' is required", nameof(appId));
            }
        }

        _appId = appId;
        _createMsgToPrimary = createMsgToPrimary;
        _onOtherInstanceOpened = onOtherInstanceOpened;
        _onServerException = onServerException;

        _options = options ?? new();
        _logger = loggerFactory?.CreateLogger<InstanceLock<TMessage>>();
        if (OperatingSystem.IsWindows())
        {
            _backend = new WindowsInstanceLock<TMessage>(_appId, _options, loggerFactory?.CreateLogger<WindowsInstanceLock<TMessage>>());
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            _backend = new UnixInstanceLock<TMessage>(_appId, _options, loggerFactory?.CreateLogger<UnixInstanceLock<TMessage>>());
        }
        else
        {
            throw new NotSupportedException($"Unsupported Platform (Operating System): {RuntimeInformation.OSDescription}.");
        }
    }

    /// <summary>
    /// <para>
    /// Tries to acquire the instance lock for the app with the app ID passed in the constructor.<br/>
    /// If it succeeds and <c>onOtherInstanceOpened</c> was provided in the constructor, the IPC server is started to listen for messages from secondary instances.<br/>
    /// </para>
    /// <para>
    /// Unlike <see cref="TryAcquireOrNotify"/>, this method does <b>not</b> notify the primary
    /// instance when acquisition fails.
    /// </para>
    /// <para>
    /// <b>Call this method on app startup as soon as possible.</b>
    /// </para>
    /// </summary>
    /// <inheritdoc cref="TryAcquireCore"/>
    public bool TryAcquire(CancellationToken ct = default) => TryAcquireCore(notify: false, ct);

    /// <summary>
    /// <para>
    /// Tries to acquire the instance lock for the app with the app ID passed in the constructor.<br/>
    /// If it succeeds and <c>onOtherInstanceOpened</c> was provided in the constructor, the IPC server is started to listen for messages from secondary instances.<br/>
    /// If it fails and <c>createMsgToPrimary</c> was provided in the constructor, meaning another instance is already running (i.e., this is a secondary instance), then it will be notified and sent the message created with <c>createMsgToPrimary</c>.
    /// </para>
    /// <para>
    /// <b>Call this method on app startup as soon as possible.</b>
    /// </para>
    /// </summary>
    /// <example>
    /// <code language="csharp">
    /// _instanceLock = new InstanceLock&lt;string&gt;(
    ///     "MyOrg.MyApp",
    ///     createMsgToPrimary: () => "ShowMainWindow",
    ///     onOtherInstanceOpened: async msg => msg == "ShowMainWindow" ? await App.ShowMainWindowAsync() : throw new NotImplementedException(),
    ///     loggerFactory: myLoggerFactory
    /// );
    /// App.OnShutdown += () => _instanceLock.Dispose();
    ///
    /// var isPrimary = _instanceLock.TryAcquireOrNotify();
    /// if (!isPrimary) App.Shutdown();
    /// </code>
    /// </example>
    /// <inheritdoc cref="TryAcquireCore"/>
    public bool TryAcquireOrNotify(CancellationToken ct = default) => TryAcquireCore(notify: true, ct);

    /// <remarks>
    /// <para>This method can be called multiple times sequentially. If the lock was already acquired by this instance, subsequent calls are cheap no-ops and immediately return <see langword="true"/>. If the lock was not acquired (another instance is primary), subsequent calls will re-attempt to acquire the lock.</para>
    /// <para>If an exception is thrown by the IPC server, <c>onServerException</c> (if provided in the constructor) is invoked. If it returns <see langword="false"/>, the server terminates permanently. If it returns <see langword="true"/> (or was not provided), the server will be restarted according to the <see cref="InstanceServerRetryPolicy"/>.</para>
    /// </remarks>
    /// <note type="threadunsafe">This method is not thread-safe.</note>
    /// <returns><see langword="true"/> if the instance lock for the app was successfully acquired, otherwise <see langword="false"/>.</returns>
    /// <exception cref="OperationCanceledException"></exception>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    private bool TryAcquireCore(bool notify, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _backend._disposed), this);

        _logger?.LogDebug("Trying to acquire instance lock... appId={AppId}, options={Options}", _appId, _options);

        ct.ThrowIfCancellationRequested();
        var isPrimary = _backend._isPrimary == true || _backend.TryAcquirePrimary();

        if (isPrimary)
        {
            _logger?.LogInformation("Acquired primary instance lock: appId={AppId}, options={Options}", _appId, _options);
            if (_onOtherInstanceOpened is not null && _pipeServerLoopTask is null) _pipeServerLoopTask = _backend.RunServerLoop(_onOtherInstanceOpened, _onServerException, ct);
        }
        else
        {
            if (notify && _createMsgToPrimary is not null)
            {
                _logger?.LogInformation("Lock already acquired - another instance is primary; attempting to notify: appId={AppId}, options={Options}", _appId, _options);
                _backend.NotifyExistingInstance(_createMsgToPrimary, ct);
            }
            else
            {
                _logger?.LogInformation("Lock already acquired - another instance is primary (not notifying): appId={AppId}, options={Options}", _appId, _options);
            }
        }

        return isPrimary;
    }

    /// <summary>Releases the instance lock and disposes the IPC server.</summary>
    /// <note type="threadsafe">Disposing is thread-safe.</note>
    // No finalizer: all native resources (mutex, flock fd, pipe handles, tokens) are held
    // indirectly through types that implement their own finalizers (Mutex, FileStream,
    // SafePipeHandle, SafeAccessTokenHandle). If the consumer forgets to Dispose, the GC
    // finalizes each wrapper individually and the OS releases the underlying handles.
    // Adding a finalizer here would only add GC overhead (extra generation survival,
    // finalization queue pressure) and risk ObjectDisposedException from touching
    // already-finalized dependents on the finalizer thread.
    public void Dispose() => _backend.Dispose();
}
