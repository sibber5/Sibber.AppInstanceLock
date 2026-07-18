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

    [InlineData(InstanceLockScope.Session, InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session, InstanceLockScope.Machine)]
    [InlineData(InstanceLockScope.User, InstanceLockScope.Session)]
    [InlineData(InstanceLockScope.User, InstanceLockScope.Machine)]
    [InlineData(InstanceLockScope.Machine, InstanceLockScope.Session)]
    [InlineData(InstanceLockScope.Machine, InstanceLockScope.User)]
    [Theory]
    public void TryAcquire_DifferentScopes_AllCombinations_AreIndependent(InstanceLockScope first, InstanceLockScope second)
    {
        var appId = UniqueAppId();

        var lock1 = CreateLock<string>(appId, options: new() { Scope = first });
        var lock2 = CreateLock<string>(appId, options: new() { Scope = second });

        lock1.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
        lock2.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
    }

    [InlineData(InstanceLockScope.Session)]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Machine)]
    [Theory]
    public void TryAcquire_SameScope_WhileMultipleOtherScopesExist_Fails(InstanceLockScope targetScope)
    {
        var appId = UniqueAppId();

        // Acquire all scopes first
        var session = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.Session });
        var user = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.User });
        var machine = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.Machine });

        session.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
        user.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
        machine.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        // Now try to acquire the target scope again
        var duplicate = CreateLock<string>(appId, options: new() { Scope = targetScope });
        duplicate.TryAcquire(TestContext.Current.CancellationToken).ShouldBeFalse();
    }
}
