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

        using var secondary = CreateLock(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask, options: options);
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(100));

        var sw = Stopwatch.StartNew();
        secondary.TryAcquireOrNotify(cts.Token).ShouldBeFalse();
        sw.Stop();

        // Should complete quickly due to cancellation, not taking the full 10 seconds of retries.
        sw.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5), "Cancellation took too long.");
    }

    [Fact]
    public async Task ConcurrentDispose_TerminatesCleanly()
    {
        var appId = UniqueAppId();
        using var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        var options = new InstanceLockOptions
        {
            NotificationRetryPolicy = new NotificationRetryPolicy(RetryAttempts: 10, MaxJitterDelay: TimeSpan.FromSeconds(5), ConnectionTimeout: TimeSpan.FromSeconds(1))
        };

        var secondary = CreateLock(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask, options: options);

        var startedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        // ReSharper disable once AccessToDisposedClosure
        var task = Task.Run(() =>
        {
            startedTcs.TrySetResult();
            return secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken);
        });

        await startedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        secondary.Dispose();

        try
        {
            var result = await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            result.ShouldBeFalse();
        }
        catch (ObjectDisposedException)
        {
            // Disposed before TryAcquireOrNotify could fully begin
        }
    }

    [InlineData(0, 0, 50, 40)] // 0 retries => 1 attempt. 50ms timeout.
    [InlineData(3, 0, 50, 150)] // 3 retries => 4 attempts. 4 * 50ms = 200ms. Minimum 150ms.
    [InlineData(1, 500, 50, 75)] // 1 retry => 2 attempts. 2 * 50ms = 100ms. Minimum 75ms.
    [InlineData(2, 300, 50, 110)] // 2 retries => 3 attempts. 3 * 50ms = 150ms. Minimum 110ms.
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

        using var secondary = CreateLock(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask, options: options);

        var sw = Stopwatch.StartNew();
        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();
        sw.Stop();

        // Ensure that it took at least the combined connection timeouts (or minimum bound).
        // Since Random jitter is applied, it could be longer, but it should never be faster
        // than the minimum bound expected from connection timeouts.
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(minElapsedMs, $"Elapsed too short: {sw.ElapsedMilliseconds}ms (Expected >= {minElapsedMs}ms)");
    }
}
