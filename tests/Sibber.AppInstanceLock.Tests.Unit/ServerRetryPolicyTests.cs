// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Pipes;
using Microsoft.Extensions.Time.Testing;

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class ServerRetryPolicyTests : UnitTestBase
{
    private sealed class MockBackendLock(InstanceLockOptions options) : InstanceLockImpl<byte>("mock_pipe", options, null, null)
    {
        public int ThrowCount;

        public override bool TryAcquirePrimary() => true;
        protected override void DisposeCore() { }

        protected override NamedPipeServerStream CreatePipeServer()
        {
            ThrowCount++;
            throw new IOException("Mock server failure");
        }
    }

    private sealed class AutoAdvancingTimeProvider : TimeProvider
    {
        public readonly FakeTimeProvider Fake = new();

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = Fake.CreateTimer(callback, state, dueTime, period);
            if (dueTime > TimeSpan.Zero && dueTime != Timeout.InfiniteTimeSpan)
            {
                ThreadPool.QueueUserWorkItem(_ =>
                {
                    Thread.Sleep(1);
                    Fake.Advance(dueTime);
                });
            }
            return timer;
        }

        public override long GetTimestamp() => Fake.GetTimestamp();
        public override long TimestampFrequency => Fake.TimestampFrequency;
        public override TimeZoneInfo LocalTimeZone => Fake.LocalTimeZone;
    }

    [InlineData(0, 50, 200, 1, 0)] // 0 retries => 1 throw, 0ms delay
    [InlineData(1, 50, 200, 2, 0)] // 1 retry => 2 throws, 0ms delay (attempt 0 -> delay 0)
    [InlineData(2, 50, 200, 3, 50)] // 2 retries => 3 throws, 50ms delay (0 + 50)
    [InlineData(3, 50, 200, 4, 150)] // 3 retries => 4 throws, 0 + 50 + 100 = 150ms delay
    [InlineData(5, 10, 20, 6, 70)] // 5 retries => 6 throws, 0 + 10 + 20 + 20 + 20 = 70ms delay
    [Theory]
    public async Task InstanceServerRetryPolicy_BackoffAndRetries_WorkCorrectly(int maxRetries, int baseDelayMs, int maxDelayMs, int expectedThrows, int expectedDelayMs)
    {
        var timeProvider = new AutoAdvancingTimeProvider();
        var startTimestamp = timeProvider.GetTimestamp();
        var options = new InstanceLockOptions
        {
            TimeProvider = timeProvider,
            InstanceServerRetryPolicy = new InstanceServerRetryPolicy(
                MinimumUptime: TimeSpan.FromSeconds(10),
                BaseDelay: TimeSpan.FromMilliseconds(baseDelayMs),
                MaxDelay: TimeSpan.FromMilliseconds(maxDelayMs),
                MaxRetries: maxRetries
            ),
        };

        using var backend = new MockBackendLock(options);
        backend._isPrimary = true;

        var serverTask = backend.RunServerLoop(
            onMessage: _ => ValueTask.CompletedTask,
            onException: null
        );

        await serverTask;

        backend.ThrowCount.ShouldBe(expectedThrows);
        timeProvider.Fake.GetElapsedTime(startTimestamp).ShouldBe(TimeSpan.FromMilliseconds(expectedDelayMs));
    }

    [Fact]
    public async Task InstanceServerRetryPolicy_OnExceptionReturnsFalse_AbortsImmediately()
    {
        var options = new InstanceLockOptions
        {
            InstanceServerRetryPolicy = new InstanceServerRetryPolicy(
                MinimumUptime: TimeSpan.FromSeconds(10),
                BaseDelay: TimeSpan.FromMilliseconds(50),
                MaxDelay: TimeSpan.FromMilliseconds(200),
                MaxRetries: 5
            ),
        };

        using var backend = new MockBackendLock(options);
        backend._isPrimary = true;

        await backend.RunServerLoop(
            onMessage: _ => ValueTask.CompletedTask,
            onException: _ => false
        );

        backend.ThrowCount.ShouldBe(1);
    }
}
