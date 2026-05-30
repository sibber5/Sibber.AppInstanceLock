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
}
