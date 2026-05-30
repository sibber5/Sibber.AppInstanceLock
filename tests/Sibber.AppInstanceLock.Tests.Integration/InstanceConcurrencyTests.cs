// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class InstanceConcurrencyTests : IntegrationTestBase
{

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Single Instance Exclusivity
    // "Only one entity can successfully return true from TryAcquirePrimary()
    //  for a given App ID and InstanceLockScope."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryAcquire_FirstInstance_ReturnsTrueAsPrimary()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);

        var result = primary.TryAcquire(TestContext.Current.CancellationToken);

        Assert.True(result);
    }

    [Fact]
    public void TryAcquire_SecondInstance_ReturnsFalse()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);
        var secondary = CreateLock<string>(appId);

        var firstResult = primary.TryAcquire(TestContext.Current.CancellationToken);
        var secondResult = secondary.TryAcquire(TestContext.Current.CancellationToken);

        Assert.True(firstResult);
        Assert.False(secondResult);
    }

    [Fact]
    public void TryAcquire_AfterPrimaryDisposed_NewInstanceBecomesPrimary()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);
        Assert.True(primary.TryAcquire(TestContext.Current.CancellationToken));
        primary.Dispose();

        var newPrimary = CreateLock<string>(appId);

        Assert.True(newPrimary.TryAcquire(TestContext.Current.CancellationToken));
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Concurrent Startup Race Condition
    // "Exactly one thread returns true (Primary) and the other returns false (Secondary)."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConcurrentStartup_ExactlyOnePrimary()
    {
        var appId = UniqueAppId();
        using var barrier = new Barrier(2);

        var results = new bool[2];

        var t1 = Task.Run(() =>
        {
            var l = CreateLock<string>(appId);
            // ReSharper disable once AccessToDisposedClosure
            barrier.SignalAndWait();
            results[0] = l.TryAcquire(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var t2 = Task.Run(() =>
        {
            var l = CreateLock<string>(appId);
            // ReSharper disable once AccessToDisposedClosure
            barrier.SignalAndWait();
            results[1] = l.TryAcquire(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        await Task.WhenAll(t1, t2);

        // Exactly one is true, exactly one is false.
        Assert.Single(results, r => r);
        Assert.Single(results, r => !r);
    }
}
