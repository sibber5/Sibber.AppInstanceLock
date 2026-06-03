// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class ConcurrencyResilienceTests : UnitTestBase
{
    [Fact]
    public void NotificationCancellation_CancelsRetryLoop()
    {
        var appId = UniqueAppId();
        using var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        var options = new InstanceLockOptions
        {
            NotificationRetryPolicy = new NotificationRetryPolicy(RetryAttempts: 10, MaxJitterDelay: TimeSpan.FromSeconds(5), ConnectionTimeout: TimeSpan.FromSeconds(1))
        };

        using var secondary = CreateLock<string>(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask, options: options);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        secondary.TryAcquireOrNotify(cts.Token).ShouldBeFalse();
        sw.Stop();

        // Should complete quickly due to cancellation, not taking the full 10 seconds of retries.
        (sw.Elapsed < TimeSpan.FromSeconds(5)).ShouldBeTrue("Cancellation took too long.");
    }

    [Fact]
    public void ConcurrentDispose_TerminatesCleanly()
    {
        var appId = UniqueAppId();
        using var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        var options = new InstanceLockOptions
        {
            NotificationRetryPolicy = new NotificationRetryPolicy(RetryAttempts: 10, MaxJitterDelay: TimeSpan.FromSeconds(5), ConnectionTimeout: TimeSpan.FromSeconds(1))
        };

        var secondary = CreateLock<string>(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask, options: options);

        _ = Task.Run(async () =>
        {
            await Task.Delay(100);
            secondary.Dispose();
        }, TestContext.Current.CancellationToken);

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    [InlineData(0, 0, 50, 40)] // 0 retries => 1 attempt. 50ms timeout.
    [InlineData(3, 0, 50, 150)] // 3 retries => 4 attempts. 4 * 50ms = 200ms. Minimum 150ms.
    [InlineData(1, 500, 10, 20)] // 1 retry => 2 attempts. Jitter up to 500ms.
    [InlineData(2, 300, 20, 50)] // 2 retries => 3 attempts. Jitter up to 300ms each.
    [Theory]
    public void NotificationRetryPolicy_ExhaustsAttempts_FollowsTiming(int retries, int maxJitterMs, int timeoutMs, int minElapsedMs)
    {
        var appId = UniqueAppId();
        // Primary holds the lock but has no onOtherInstance, so NO server pipe is listening!
        using var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        var options = new InstanceLockOptions
        {
            NotificationRetryPolicy = new NotificationRetryPolicy(
                RetryAttempts: retries,
                MaxJitterDelay: TimeSpan.FromMilliseconds(maxJitterMs),
                ConnectionTimeout: TimeSpan.FromMilliseconds(timeoutMs))
        };

        using var secondary = CreateLock<string>(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask, options: options);

        var sw = Stopwatch.StartNew();
        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();
        sw.Stop();

        // Ensure that it took at least the combined connection timeouts (or minimum bound).
        // Since Random jitter is applied, it could be longer, but it should never be faster
        // than the minimum bound expected from connection timeouts.
        (sw.ElapsedMilliseconds >= minElapsedMs).ShouldBeTrue($"Elapsed too short: {sw.ElapsedMilliseconds}ms (Expected >= {minElapsedMs}ms)");
    }
}
