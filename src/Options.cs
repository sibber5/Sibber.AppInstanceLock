// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock;

/// <summary>
/// Defines the scope of the lock for the single-instance application.
/// </summary>
/// <remarks>
/// This enumeration is used to specify the extent to which the lock applies, ensuring that only one instance of the application or process operates within the defined scope.
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
public sealed record InstanceLockOptions
{
    /// <summary>
    /// Specifies the scope of the lock, determining whether the lock is limited to the current session, user, or machine.
    /// </summary>
    public InstanceLockScope Scope { get; init; } = InstanceLockScope.Session;

    /// <summary>
    /// Configures the retry policy for attempts to notify the primary instance when another instance tries to acquire the lock.
    /// </summary>
    public NotificationRetryPolicy NotificationRetryPolicy { get; init; } = new(RetryAttempts: 4, MaxJitterDelay: TimeSpan.FromMilliseconds(300), ConnectionTimeout: TimeSpan.FromMilliseconds(1500));

    /// <summary>
    /// Configures the retry policy for restarting the IPC server that listens for notifications from other instances that try to acquire the lock, in case of a crash.
    /// </summary>
    public InstanceServerRetryPolicy InstanceServerRetryPolicy { get; init; } = new(MinimumUptime: TimeSpan.FromSeconds(3), BaseDelay: TimeSpan.FromMilliseconds(300), MaxDelay: TimeSpan.FromSeconds(5));
}

/// <inheritdoc cref="InstanceLockOptions.NotificationRetryPolicy"/>
public sealed record NotificationRetryPolicy
{
    /// <param name="RetryAttempts">
    /// The number of retry attempts.
    /// </param>
    /// <param name="MaxJitterDelay">
    /// The maximum delay duration between retry attempts.<br/>
    /// The actual wait time before each retry is randomized (jittered) uniformly between <see cref="TimeSpan.Zero"/> and this maximum value to avoid synchronized retry waves.
    /// </param>
    /// <param name="ConnectionTimeout">
    /// The connection timeout for connecting to the primary instance's IPC server.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">One or more of <paramref name="RetryAttempts"/>, <paramref name="MaxJitterDelay"/>, or <paramref name="ConnectionTimeout"/> is negative.</exception>
    public NotificationRetryPolicy(int RetryAttempts, TimeSpan MaxJitterDelay, TimeSpan ConnectionTimeout)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(RetryAttempts);
        this.RetryAttempts = RetryAttempts;
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxJitterDelay, TimeSpan.Zero);
        this.MaxJitterDelay = MaxJitterDelay;
        ArgumentOutOfRangeException.ThrowIfLessThan(ConnectionTimeout, TimeSpan.Zero);
        this.ConnectionTimeout = ConnectionTimeout;
    }

    public void Deconstruct(out int RetryAttempts, out TimeSpan MaxJitterDelay, out TimeSpan ConnectionTimeout)
    {
        RetryAttempts = this.RetryAttempts;
        MaxJitterDelay = this.MaxJitterDelay;
        ConnectionTimeout = this.ConnectionTimeout;
    }

    /// <summary>
    /// The number of retry attempts.
    /// </summary>
    public int RetryAttempts { get; }

    /// <summary>
    /// The maximum delay duration between retry attempts.<br/>
    /// The actual wait time before each retry is randomized (jittered) uniformly between <see cref="TimeSpan.Zero"/> and this maximum value to avoid synchronized retry waves.
    /// </summary>
    public TimeSpan MaxJitterDelay { get; }

    /// <summary>
    /// The connection timeout for connecting to the primary instance's IPC server.
    /// </summary>
    public TimeSpan ConnectionTimeout { get; }

    public static readonly NotificationRetryPolicy DontRetry = new(RetryAttempts: 0, default, default);
}

/// <inheritdoc cref="InstanceLockOptions.InstanceServerRetryPolicy"/>
public sealed record InstanceServerRetryPolicy
{
    /// <param name="MinimumUptime">
    /// The minimum duration the server must have been running before its uptime is considered "stable".<br/>
    /// When the server has been running for at least this duration before a failure occurs, the backoff delay resets to zero
    /// (the next restart attempt happens immediately, and subsequent failures start the exponential backoff from <see cref="BaseDelay"/> again).
    /// </param>
    /// <param name="BaseDelay">
    /// The base delay used for exponential backoff. The first restart attempt after a failure happens immediately (zero delay).
    /// On the second consecutive failure, the delay is set to this value. Each subsequent consecutive failure doubles the delay, up to <see cref="MaxDelay"/>.
    /// </param>
    /// <param name="MaxDelay">
    /// The ceiling on the exponential backoff delay. Once the calculated delay reaches this value, it remains capped here for all subsequent retry attempts.
    /// </param>
    /// <param name="MaxRetries">
    /// The maximum number of consecutive restart attempts allowed before the server loop terminates permanently. Set to <c>-1</c> for unlimited retries. The counter resets to zero each time the server achieves <see cref="MinimumUptime"/>.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="MinimumUptime"/> is less than <see cref="TimeSpan.Zero"/><br/>
    /// - OR -<br/>
    /// <paramref name="BaseDelay"/> is less than <see cref="TimeSpan.Zero"/><br/>
    /// - OR -<br/>
    /// <paramref name="MaxDelay"/> is less than <paramref name="BaseDelay"/><br/>
    /// - OR -<br/>
    /// <paramref name="MaxRetries"/> is less than <c>-1</c> (<c>-1</c> is unlimited)
    /// </exception>
    public InstanceServerRetryPolicy(TimeSpan MinimumUptime, TimeSpan BaseDelay, TimeSpan MaxDelay, int MaxRetries = -1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(MinimumUptime, TimeSpan.Zero);
        this.MinimumUptime = MinimumUptime;
        ArgumentOutOfRangeException.ThrowIfLessThan(BaseDelay, TimeSpan.Zero);
        this.BaseDelay = BaseDelay;
        // manually throw instead of ArgumentOutOfRangeException.ThrowIfLessThan because we want to specify the name of BaseDelay in the exception message.
        if (MaxDelay < BaseDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxDelay), MaxDelay, $"{nameof(MaxDelay)} ('{MaxDelay}') must be greater than or equal to {nameof(BaseDelay)} ('{BaseDelay}').");
        }
        this.MaxDelay = MaxDelay;
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxRetries, -1);
        this.MaxRetries = MaxRetries;
    }

    public void Deconstruct(out TimeSpan MinimumUptime, out TimeSpan BaseDelay, out TimeSpan MaxDelay, out int MaxRetries)
    {
        MinimumUptime = this.MinimumUptime;
        BaseDelay = this.BaseDelay;
        MaxDelay = this.MaxDelay;
        MaxRetries = this.MaxRetries;
    }

    /// <summary>
    /// The minimum duration the server must have been running before its uptime is considered "stable".<br/>
    /// When the server has been running for at least this duration before a failure occurs, the backoff delay resets to zero
    /// (the next restart attempt happens immediately, and subsequent failures start the exponential backoff from <see cref="BaseDelay"/> again).
    /// </summary>
    public TimeSpan MinimumUptime { get; }

    /// <summary>
    /// The base delay used for exponential backoff. The first restart attempt after a failure happens immediately (zero delay).
    /// On the second consecutive failure, the delay is set to this value. Each subsequent consecutive failure doubles the delay, up to <see cref="MaxDelay"/>.
    /// </summary>
    public TimeSpan BaseDelay { get; }

    /// <summary>
    /// The ceiling on the exponential backoff delay. Once the calculated delay reaches this value, it remains capped here for all subsequent retry attempts.
    /// </summary>
    public TimeSpan MaxDelay { get; }

    /// <summary>
    /// The maximum number of consecutive restart attempts allowed before the server loop terminates permanently. Set to <c>-1</c> for unlimited retries. The counter resets to zero each time the server achieves <see cref="MinimumUptime"/>.
    /// </summary>
    public int MaxRetries { get; }

    public static readonly InstanceServerRetryPolicy DontRetry = new(default, default, default, MaxRetries: 0);
}
