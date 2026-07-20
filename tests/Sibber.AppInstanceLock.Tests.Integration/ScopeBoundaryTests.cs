// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class ScopeBoundaryTests : IntegrationTestBase
{
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

    [Fact]
    public void TryAcquire_SessionScope_DifferentSessions_AreIndependent()
    {
        var appId = UniqueAppId();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsInstanceLockHooks._sessionIdHook.Value = () => 100;
            }
            else
            {
                UnixInstanceLockHooks._sessionIdHook.Value = () => "session-100";
            }
            var session1 = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.Session });
            session1.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

            if (OperatingSystem.IsWindows())
            {
                WindowsInstanceLockHooks._sessionIdHook.Value = () => 200;
            }
            else
            {
                UnixInstanceLockHooks._sessionIdHook.Value = () => "session-200";
            }
            var session2 = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.Session });
            session2.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsInstanceLockHooks._sessionIdHook.Value = null;
            }
            else
            {
                UnixInstanceLockHooks._sessionIdHook.Value = null;
            }
        }
    }

    [Fact]
    public void TryAcquire_UserScope_DifferentUsers_AreIndependent()
    {
        var appId = UniqueAppId();

        try
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsInstanceLockHooks._userIdHook.Value = () => "S-1-5-21-1234567890-1234567890-1234567890-1001";
            }
            else
            {
                UnixInstanceLockHooks._userIdHook.Value = () => 1001;
            }
            var user1 = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.User });
            user1.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

            if (OperatingSystem.IsWindows())
            {
                WindowsInstanceLockHooks._userIdHook.Value = () => "S-1-5-21-1234567890-1234567890-1234567890-1002";
            }
            else
            {
                UnixInstanceLockHooks._userIdHook.Value = () => 1002;
            }
            var user2 = CreateLock<string>(appId, options: new() { Scope = InstanceLockScope.User });
            user2.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();
        }
        finally
        {
            if (OperatingSystem.IsWindows())
            {
                WindowsInstanceLockHooks._userIdHook.Value = null;
            }
            else
            {
                UnixInstanceLockHooks._userIdHook.Value = null;
            }
        }
    }
}
