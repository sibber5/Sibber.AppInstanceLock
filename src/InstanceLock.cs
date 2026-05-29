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
/// Defines the scope of the lock for the single-instance application.
/// </summary>
/// <remarks>
/// This enumeration is used to specify the extent to which the lock applies,
/// ensuring that only one instance of the application or process operates
/// within the defined scope.
/// </remarks>
public enum InstanceLockScope
{
    /// <summary>
    /// Specifies that the lock applies only to the user logon session; Meaning other instances of the app can run under the same user, each in their own logon session.
    /// </summary>
    Session = 0,

    /// <summary>
    /// Specifies that the lock applies to the current user. Other instances will not be allowed under the same user even if from a separate session.
    /// </summary>
    User,

    /// <summary>
    /// Specifies that the lock applies machine-wide. No other instances of the app will be allowed, whether under a different session or user.
    /// </summary>
    Machine,
}

/// <summary>
/// Specifies the configuration options for <see cref="InstanceLock{TMessage}"/>.
/// </summary>
public record InstanceLockOptions
{
    /// <summary>
    /// Specifies the scope of the lock, determining whether the lock is limited to the current session, user, or machine.
    /// </summary>
    public InstanceLockScope Scope { get; init; } = InstanceLockScope.Session;

    /// <summary>
    /// Configures the retry policy for attempts to notify the primary instance when another instance tries to acquire the lock.
    /// </summary>
    public NotifyInstanceRetryPolicy NotifyInstanceRetryPolicy { get; init; } = new(Attempts: 4, Delay: TimeSpan.FromMilliseconds(300), ConnectionTimeout: TimeSpan.FromMilliseconds(1500));

    /// <summary>
    /// Configures the retry policy for restarting the IPC server that listens for notifications from other instances that try to acquire the lock, in case of a crash.
    /// </summary>
    public InstanceServerRetryPolicy InstanceServerRetryPolicy { get; init; } = new(MinimumUptime: TimeSpan.FromSeconds(3), InitialDelay: TimeSpan.FromMilliseconds(300), MaxDelay: TimeSpan.FromSeconds(5));
}

/// <inheritdoc cref="InstanceLockOptions.NotifyInstanceRetryPolicy"/>
/// <param name="Attempts">
/// The number of retry attempts.
/// </param>
/// <param name="Delay">
/// The maximum delay duration between retry attempts.<br/>The actual wait time before each retry is randomized (jittered) uniformly between <see cref="TimeSpan.Zero"/> and this maximum value to avoid synchronized retry waves.
/// </param>
/// <param name="ConnectionTimeout">
/// The connection timeout for connecting to the primary instance's IPC server.
/// </param>
public record NotifyInstanceRetryPolicy(int Attempts, TimeSpan Delay, TimeSpan ConnectionTimeout)
{
    public static readonly NotifyInstanceRetryPolicy DontRetry = new(Attempts: 0, default, default);
}

/// <inheritdoc cref="InstanceLockOptions.InstanceServerRetryPolicy"/>
/// <param name="MinimumUptime">
/// The minimum duration the server must have been running before its uptime is considered "stable."
/// When the server has been running for at least this duration before a failure occurs, the backoff delay resets to zero
/// (the next restart attempt happens immediately, and subsequent failures start the exponential backoff from <see cref="InitialDelay"/> again).
/// </param>
/// <param name="InitialDelay">The initial delay before attempting to restart the server after the first failure. Each subsequent consecutive failure doubles the delay, up to <see cref="MaxDelay"/>. The very first restart attempt always happens immediately (zero delay).</param>
/// <param name="MaxDelay">The ceiling on the exponential backoff delay. Once the calculated delay reaches this value, it remains capped here for all subsequent retry attempts.</param>
/// <param name="MaxRetries">The maximum number of consecutive restart attempts allowed before the server loop terminates permanently. Set to <c>-1</c> for unlimited retries. The counter resets to zero each time the server achieves <see cref="MinimumUptime"/>.</param>
public record InstanceServerRetryPolicy(TimeSpan MinimumUptime, TimeSpan InitialDelay, TimeSpan MaxDelay, int MaxRetries = -1)
{
    public static readonly InstanceServerRetryPolicy DontRetry = new(default, default, default, MaxRetries: 0);
}

/// <summary>
/// Manages the acquisition of an application instance lock to ensure that only a single instance
/// of the application is running at a time within a given context (referred to as the primary instance).<br/>
/// Optionally, it allows sending a message or notifying to the primary instance when another instance is attempted to be opened.
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
    private readonly InstanceLockImpl<TMessage> _backend;

    private readonly Func<TMessage>? _getMessageToPrimary;
    private readonly Func<TMessage, ValueTask>? _onOtherInstanceOpened;
    private readonly Func<Exception, bool>? _onServerException;

    private Task? _pipeServerLoopTask;

    /// <param name="appId">
    /// <para>A globally unique ID for the application. This can be the ID of your application's package, or a GUID, or a mix of both. The ID must only consist alphanumeric characters, '-', and '_'; Invalid characters will be sanitized.</para>
    /// <para>If <see langword="null"/>, an ID will be generated automatically.</para>
    /// </param>
    /// <param name="getMessageToPrimary">
    /// Gets the message that will be sent to the primary instance if acquiring the instance lock failed (meaning this is not the primary instance).<br/>See summary of <see cref="TryAcquireOrNotify"/> for more details.
    /// </param>
    /// <param name="onOtherInstanceOpened">
    /// A call back that is invoked in the primary instance when another instance is attempted to be opened.<br/>See summary of <see cref="TryAcquireOrNotify"/> for more details.
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
    /// <para>One of the parameters <paramref name="getMessageToPrimary"/> and <paramref name="onOtherInstanceOpened"/> is <see langword="null"/> but the other one is not.</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="appId"/> is empty, or consists only of white-space characters.
    /// </exception>
    /// <exception cref="NotSupportedException"></exception>
    /// <exception cref="System.Security.SecurityException"></exception>
    // ExceptionAdjustment: M:System.Guid.ToString(System.String) -T:System.FormatException
    public InstanceLock(string? appId, Func<TMessage>? getMessageToPrimary = null, Func<TMessage, ValueTask>? onOtherInstanceOpened = null, Func<Exception, bool>? onServerException = null, ILoggerFactory? loggerFactory = null, InstanceLockOptions? options = null)
    {
        if (onOtherInstanceOpened is null && getMessageToPrimary is not null) throw new ArgumentNullException(nameof(onOtherInstanceOpened), $"{nameof(onOtherInstanceOpened)} is null, but {nameof(getMessageToPrimary)} is not null.");
        if (getMessageToPrimary is null && onOtherInstanceOpened is not null) throw new ArgumentNullException(nameof(getMessageToPrimary), $"{nameof(getMessageToPrimary)} is null, but {nameof(onOtherInstanceOpened)} is not null.");

        _getMessageToPrimary = getMessageToPrimary;
        _onOtherInstanceOpened = onOtherInstanceOpened;
        _onServerException = onServerException;

        if (appId is null)
        {
            _appId = Guid.NewGuid().ToString("N");
        }
        else
        {
            _appId = appId.Sanitize();
            if (string.IsNullOrWhiteSpace(_appId)) throw new ArgumentException($"Invalid sanitized app ID value: '{_appId}'. Unique {nameof(appId)} required", nameof(appId));
        }
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

        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { Dispose(); } catch { } };
        AppDomain.CurrentDomain.UnhandledException += (_, _) => { try { Dispose(); } catch { } };
    }

    /// <summary>
    /// <para>
    /// Tries to acquire the instance lock for the app with the app ID passed in the constructor.<br/>
    /// If it succeeds then <c>onOtherInstanceOpened</c> (passed in the constructor) will be registered to be called if another instance tries to acquire the lock.<br/>
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
    /// If it succeeds then <c>onOtherInstanceOpened</c> (passed in the constructor) will be registered to be called if another instance tries to acquire the lock.<br/>
    /// If it fails to acquire the lock, meaning another instance is already running (this isn't the primary), then it will be notified and sent the message created with <c>createNotificationMessage</c> (passed in the constructor).
    /// </para>
    /// <para>
    /// <b>Call this method on app startup as soon as possible.</b>
    /// </para>
    /// </summary>
    /// <example>
    /// <code language="csharp">
    /// _instanceLock = new InstanceLock&lt;string&gt;(
    ///     "MyOrg.MyApp",
    ///     getMessageToPrimary: () => "ShowMainWindow",
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
    // ExceptionAdjustment: M:System.Threading.Interlocked.Exchange``1(``0@,``0) -T:System.NotSupportedException
    public bool TryAcquireOrNotify(CancellationToken ct = default) => TryAcquireCore(notify: true, ct);

    /// <remarks>
    /// <para>This method can be called multiple times sequentially. If the lock was already acquired by this instance, subsequent calls are cheap no-ops and immediately return <see langword="true"/>. If the lock was not acquired (another instance is primary), subsequent calls will re-attempt to acquire the lock. When notifying, each failed attempt will invoke the message delegate and send a new message to the primary instance.</para>
    /// <para>If an exception is thrown by the IPC server that listens for notifications from other instances that try to acquire the lock, <c>onServerException</c> (passed in the constructor) will be invoked and then the exception is swallowed.</para>
    /// </remarks>
    /// <note type="threadunsafe">This method is not thread-safe.</note>
    /// <returns><see langword="true"/> if the instance lock for the app was successfully acquired, otherwise <see langword="false"/>.</returns>
    /// <exception cref="OperationCanceledException"></exception>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    private bool TryAcquireCore(bool notify, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _backend._disposed), this);

        _logger?.LogDebug(nameof(TryAcquireOrNotify) + ": trying to acquire instance lock... appId={AppId}, options={Options}", _appId, _options);

        ct.ThrowIfCancellationRequested();
        var isPrimary = _backend._isPrimary == true || _backend.TryAcquirePrimary();

        if (isPrimary)
        {
            _logger?.LogInformation("Acquired primary instance lock: appId={AppId}, options={Options}", _appId, _options);
            if (_onOtherInstanceOpened is not null && _pipeServerLoopTask is null) _pipeServerLoopTask = _backend.RunServerLoop(_onOtherInstanceOpened, _onServerException, ct);
        }
        else
        {
            if (notify && _getMessageToPrimary is not null)
            {
                _logger?.LogInformation("Lock already acquired - another instance is primary; attempting to notify: appId={AppId}, options={Options}", _appId, _options);
                _backend.NotifyExistingInstance(_getMessageToPrimary, ct);
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
    // ExceptionAdjustment: M:System.Threading.Interlocked.Exchange``1(``0@,``0) -T:System.NotSupportedException
    public void Dispose() => _backend.Dispose();
}
