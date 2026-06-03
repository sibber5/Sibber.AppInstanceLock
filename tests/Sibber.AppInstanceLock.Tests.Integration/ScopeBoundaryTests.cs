// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class ScopeBoundaryTests : IntegrationTestBase
{

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Scope isolation (same-user, same-session, same-machine)
    // All three scopes within the same process produce distinct lock names.
    // ──────────────────────────────────────────────────────────────────────

    [InlineData(InstanceLockScope.Session)]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Machine)]
    [Theory]
    public void TryAcquire_EachScope_AcquiresSuccessfully(InstanceLockScope scope)
    {
        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };
        var primary = CreateLock<string>(appId, options: options);

        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
    }

    [Fact]
    public void TryAcquire_DifferentScopes_SameAppId_AreIndependent()
    {
        var appId = UniqueAppId();

        var session = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.Session });
        var machine = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.Machine });

        // Both should acquire ─ they are separate lock names.
        session.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
        machine.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
    }
}
