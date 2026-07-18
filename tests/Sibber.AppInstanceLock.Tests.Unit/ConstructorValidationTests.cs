// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Sibber.AppInstanceLock.Tests.Shared;

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class ConstructorValidationTests : UnitTestBase
{
    [Fact]
    public void Constructor_NullAppId_Throws() => Should.Throw<ArgumentNullException>(() => new InstanceLock<byte>(null!));

    [Fact]
    public void Constructor_EmptyAppId_Throws() => Should.Throw<ArgumentException>(() => new InstanceLock<byte>(""));

    [Fact]
    public void Constructor_InvalidCharsInAppId_Throws()
    {
        Should.Throw<ArgumentException>(() => new InstanceLock<byte>("my.app"));
        Should.Throw<ArgumentException>(() => new InstanceLock<byte>("my app"));
        Should.Throw<ArgumentException>(() => new InstanceLock<byte>("app/id"));
    }

    [Fact]
    public void Constructor_AppIdTooLong_Throws()
    {
        var tooLong = new string('a', 256);
        Should.Throw<ArgumentOutOfRangeException>(() => new InstanceLock<byte>(tooLong));
    }

    [Fact]
    public void Constructor_OnlyOneMsgCallbackProvided_Throws()
    {
        // createMsg without onOtherInstance
        Should.Throw<ArgumentNullException>(() => new InstanceLock<byte>("valid-id", createMsgToPrimary: () => 1));

        // onOtherInstance without createMsg
        Should.Throw<ArgumentNullException>(() => new InstanceLock<byte>("valid-id", onOtherInstanceOpened: _ => ValueTask.CompletedTask));
    }

    [InlineData(-1, 10, 10)]
    [InlineData(10, -1, 10)]
    [InlineData(10, 10, -1)]
    [Theory]
    public void NotificationRetryPolicy_NegativeValues_Throws(int attempts, int jitter, int timeout)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new NotificationRetryPolicy(
            RetryAttempts: attempts,
            MaxJitterDelay: TimeSpan.FromMilliseconds(jitter),
            ConnectionTimeout: TimeSpan.FromMilliseconds(timeout))
        );
    }

    [InlineData(-1, 10, 10, 10)]
    [InlineData(10, -1, 10, 10)]
    [InlineData(10, 10, -1, 10)]
    [InlineData(10, 10, 10, -2)]
    [Theory]
    public void InstanceServerRetryPolicy_NegativeValues_Throws(int uptime, int baseDelay, int maxDelay, int retries)
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new InstanceServerRetryPolicy(
            MinimumUptime: TimeSpan.FromMilliseconds(uptime),
            BaseDelay: TimeSpan.FromMilliseconds(baseDelay),
            MaxDelay: TimeSpan.FromMilliseconds(maxDelay),
            MaxRetries: retries)
        );
    }

    [Fact]
    public void InstanceServerRetryPolicy_MaxDelayLessThanBaseDelay_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new InstanceServerRetryPolicy(
            MinimumUptime: TimeSpan.FromSeconds(1),
            BaseDelay: TimeSpan.FromMilliseconds(100),
            MaxDelay: TimeSpan.FromMilliseconds(50))
        );
    }

    [WindowsFact]
    public void WindowsBackendLock_InvalidScope_Throws()
    {
        var options = new InstanceLockOptions { Scope = (InstanceLockScope)99 };
        Should.Throw<NotSupportedException>(() => new InstanceLock<byte>("test-id", options: options));
    }

    [UnixFact]
    public void UnixBackendLock_InvalidScope_Throws()
    {
        var options = new InstanceLockOptions { Scope = (InstanceLockScope)99 };
        Should.Throw<NotSupportedException>(() => new InstanceLock<byte>("test-id", options: options));
    }
}
