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
/// The delay after each retry attempt.
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
/// The minimum uptime for a retry to be considered. If the server has been up for less, then it will not be restarted.<br/>
/// (This, of course, only applies if a restart is not being attempted.)
/// </param>
/// <param name="InitialDelay">The initial time to wait before attempting to restart the server again after the first attempt. Every following attempt will delay by double the amount delayed the previous attempt. (the first attempt will always happen instantly.)</param>
/// <param name="MaxDelay">The maximum retry delay. The server will continue to be restarted while the current delay (the time to wait before attempting to restart again) is less than or equal to this value. After each failed attempt, the delay is doubled until it reaches this limit.</param>
public record InstanceServerRetryPolicy(TimeSpan MinimumUptime, TimeSpan InitialDelay, TimeSpan MaxDelay)
{
    public static readonly InstanceServerRetryPolicy DontRetry = new(Timeout.InfiniteTimeSpan, default, default);
}

#if DOCFX
/// <remarks>
/// <![CDATA[
/// > [!IMPORTANT]
/// > Acquiring the instance lock must be done on app startup as soon as possible, and only once.
/// ]]>
/// </remarks>
#endif
/// <summary>
/// Manages the acquisition of an application instance lock to ensure that only a single instance
/// of the application is running at a time within a given context (referred to as the primary instance).<br/>
/// Optionally, it allows sending a message or notifying to the primary instance when another instance is attempted to be opened.
/// </summary>
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

    private readonly Func<TMessage>? _createNotificationMessage;
    private readonly Func<TMessage, ValueTask>? _onOtherInstanceOpened;
    private readonly Func<Exception, bool>? _onServerException;

    /// <param name="appId">A globally unique ID for the application. This can be the ID of your application's package, or a GUID, or a mix of both.</param>
    /// <param name="createNotificationMessage">
    /// A factory that creates the message to be sent to the primary instance. This is only invoked in other instances when they are opened.<br/>See summary of <see cref="TryAcquireOrNotify"/> for more details.
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
    /// <para><paramref name="appId"/> is <see langword="null"/>.</para>
    /// <para>- OR -</para>
    /// <para>One of the parameters <paramref name="createNotificationMessage"/> and <paramref name="onOtherInstanceOpened"/> is <see langword="null"/> but the other one is not.</para>
    /// </exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="appId"/> is empty, or consists only of white-space characters.
    /// </exception>
    /// <exception cref="NotSupportedException"></exception>
    /// <exception cref="System.Security.SecurityException"></exception>
    public InstanceLock(string appId, Func<TMessage>? createNotificationMessage = null, Func<TMessage, ValueTask>? onOtherInstanceOpened = null, Func<Exception, bool>? onServerException = null, ILoggerFactory? loggerFactory = null, InstanceLockOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(appId);
        if (onOtherInstanceOpened is null && createNotificationMessage is not null) throw new ArgumentNullException(nameof(onOtherInstanceOpened), $"{nameof(onOtherInstanceOpened)} is null, but {nameof(createNotificationMessage)} is not null.");
        if (createNotificationMessage is null && onOtherInstanceOpened is not null) throw new ArgumentNullException(nameof(createNotificationMessage), $"{nameof(createNotificationMessage)} is null, but {nameof(onOtherInstanceOpened)} is not null.");

        _createNotificationMessage = createNotificationMessage;
        _onOtherInstanceOpened = onOtherInstanceOpened;
        _onServerException = onServerException;

        _appId = appId.Sanitize();
        if (string.IsNullOrWhiteSpace(_appId)) throw new ArgumentException($"Invalid sanitized app ID value: '{_appId}'. Unique {nameof(appId)} required", nameof(appId));
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
    /// <returns><see langword="true"/> if the instance lock for the app was successfully acquired, otherwise <see langword="false"/>.</returns>
    /// <example>
    /// <code language="csharp">
    /// _instanceLock = new InstanceLock&lt;string&gt;(
    ///     "MyOrg.MyApp",
    ///     createNotificationMessage: () => "ShowMainWindow",
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

#if DOCFX // TODO: figure out how to do this without having to copy the entire doc comment.
    /// <remarks>
    /// <para>If an exception is thrown by the IPC server that listens for notifications from other instances that try to acquire the lock, <c>onServerException</c> (passed in the constructor) will be invoked and then the exception is swallowed.</para>
    /// <para>
    /// <![CDATA[
    /// > [!THREADUNSAFE]
    /// > TryAcquireOrNotify is not thread-safe.
    /// ]]>
    /// </para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"></exception>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
#else
    /// <remarks>
    /// <para>If an exception is thrown by the IPC server that listens for notifications from other instances that try to acquire the lock, <c>onServerException</c> (passed in the constructor) will be invoked and then the exception is swallowed.</para>
    /// <para><b>This method is not thread-safe.</b></para>
    /// </remarks>
    /// <exception cref="OperationCanceledException"></exception>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
#endif
    private bool TryAcquireCore(bool notify, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _backend._disposed), this);

        _logger?.LogDebug(nameof(TryAcquireOrNotify) + ": trying to acquire instance lock... appId={AppId}, options={Options}", _appId, _options);

        ct.ThrowIfCancellationRequested();
        var isPrimary = _backend._isPrimary == true || _backend.TryAcquirePrimary();

        if (isPrimary)
        {
            _logger?.LogInformation("Acquired primary instance lock: appId={AppId}, options={Options}", _appId, _options);
            if (_onOtherInstanceOpened is not null) _ = _backend.RunServerLoop(_onOtherInstanceOpened, _onServerException, ct);
        }
        else
        {
            if (notify && _createNotificationMessage is not null)
            {
                _logger?.LogInformation("Lock already acquired - another instance is primary; attempting to notify: appId={AppId}, options={Options}", _appId, _options);
                _backend.NotifyExistingInstance(_createNotificationMessage, ct);
            }
            else
            {
                _logger?.LogInformation("Lock already acquired - another instance is primary (not notifying): appId={AppId}, options={Options}", _appId, _options);
            }
        }

        return isPrimary;
    }

#if DOCFX
    /// <remarks>
    /// <![CDATA[
    /// > [!THREADSAFE]
    /// > Dispose() is thread-safe.
    /// ]]>
    /// </remarks>
#else
    /// <remarks>
    /// <see cref="Dispose"/> is thread-safe.
    /// </remarks>
#endif
    // ExceptionAdjustment: M:System.Threading.Interlocked.Exchange``1(``0@,``0) -T:System.NotSupportedException
    public void Dispose() => _backend.Dispose();
}
