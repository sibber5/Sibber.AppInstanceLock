// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class StateIdempotencyTests : UnitTestBase
{

    [Fact]
    public void TryAcquire_SubsequentCallOnPrimary_ReturnsTrue()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        // Subsequent calls to TryAcquire on the same instance should immediately return true.
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        // multiple disposes must not throw
        primary.Dispose();
        primary.Dispose();
        primary.Dispose();
    }

    [Fact]
    public void Dispose_ThenTryAcquire_ThrowsObjectDisposed()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);
        primary.Dispose();

        Should.Throw<ObjectDisposedException>(() => primary.TryAcquire(TestContext.Current.CancellationToken));
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: CancellationToken respected by TryAcquire
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryAcquire_CancelledToken_ThrowsOperationCanceled()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Should.Throw<OperationCanceledException>(() => primary.TryAcquire(cts.Token));
    }

    [Fact]
    public void TryAcquireOrNotify_SubsequentCallOnPrimary_ReturnsTrue_NoCallback()
    {
        var appId = UniqueAppId();
        var callCount = 0;
        using var primary = CreateLock(appId, createMsg: () =>
        {
            callCount++;
            return "msg";
        }, onOtherInstance: _ => ValueTask.CompletedTask);

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();
        callCount.ShouldBe(0); // not called because it's primary

        // Subsequent calls
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();
        callCount.ShouldBe(0);
    }

    [Fact]
    public async Task TryAcquireOrNotify_SubsequentCallOnSecondary_ReturnsFalse_AndNotifies()
    {
        var appId = UniqueAppId();
        var receivedCount = 0;
        var firstMsgTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMsgTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var primary = CreateLock(appId, createMsg: () => "msg", onOtherInstance: _ =>
        {
            // ReSharper disable once AccessToModifiedClosure
            var count = Interlocked.Increment(ref receivedCount);
            if (count == 1) firstMsgTcs.TrySetResult();
            else if (count == 2) secondMsgTcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        using var secondary = CreateLock(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask);

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();

        // Wait for primary to process
        await firstMsgTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref receivedCount).ShouldBe(1);

        // Subsequent call on secondary
        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();
        await secondMsgTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Volatile.Read(ref receivedCount).ShouldBe(2);
    }
}
