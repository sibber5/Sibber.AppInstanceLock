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
        Assert.True(primary.TryAcquire(TestContext.Current.CancellationToken));

        // Subsequent calls to TryAcquire on the same instance should immediately return true.
        Assert.True(primary.TryAcquire(TestContext.Current.CancellationToken));
        Assert.True(primary.TryAcquire(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);
        Assert.True(primary.TryAcquire(TestContext.Current.CancellationToken));

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

        Assert.Throws<ObjectDisposedException>(() => primary.TryAcquire(TestContext.Current.CancellationToken));
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

        Assert.Throws<OperationCanceledException>(() => primary.TryAcquire(cts.Token));
    }

    [Fact]
    public void TryAcquireOrNotify_SubsequentCallOnPrimary_ReturnsTrue_NoCallback()
    {
        var appId = UniqueAppId();
        var callCount = 0;
        using var primary = CreateLock<string>(appId, createMsg: () =>
        {
            callCount++;
            return "msg";
        }, onOtherInstance: _ => ValueTask.CompletedTask);

        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));
        Assert.Equal(0, callCount); // not called because it's primary

        // Subsequent calls
        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));
        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));
        Assert.Equal(0, callCount);
    }

    [Fact]
    public async Task TryAcquireOrNotify_SubsequentCallOnSecondary_ReturnsFalse_AndNotifies()
    {
        var appId = UniqueAppId();
        var receivedCount = 0;
        using var primary = CreateLock<string>(appId, createMsg: () => "msg", onOtherInstance: _ =>
        {
            // ReSharper disable once AccessToModifiedClosure
            Interlocked.Increment(ref receivedCount);
            return ValueTask.CompletedTask;
        });
        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        using var secondary = CreateLock<string>(appId, createMsg: () => "msg", onOtherInstance: _ => ValueTask.CompletedTask);

        Assert.False(secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        // Wait for primary to process
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(1, Interlocked.CompareExchange(ref receivedCount, 0, 0));

        // Subsequent call on secondary
        Assert.False(secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.Equal(2, Interlocked.CompareExchange(ref receivedCount, 0, 0));
    }
}
