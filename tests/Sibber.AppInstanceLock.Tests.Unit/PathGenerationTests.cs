// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Sibber.AppInstanceLock.Tests.Shared;

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class PathGenerationTests : UnitTestBase
{
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Machine)]
    [InlineData(InstanceLockScope.Session)]
    [WindowsTheory]
    public void Windows_PathGeneration_And_Isolation(InstanceLockScope scope)
    {
        WindowsInstanceLockHooks._userIdHook.Value = () => "S-1-5-21-1234567890-1234567890-1234567890-1001";
        WindowsInstanceLockHooks._sessionIdHook.Value = () => 2;

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };
        using var inst = new WindowsInstanceLock<string>(appId, options, null);

        inst.TryAcquirePrimary().ShouldBeTrue();

        if (scope == InstanceLockScope.User)
        {
            inst._mutexName.ShouldContain("user_S-1-5-21-1234567890-1234567890-1234567890-1001");
        }
        else if (scope == InstanceLockScope.Session)
        {
            inst._mutexName.ShouldEndWith("Local\\" + appId);
        }
        else if (scope == InstanceLockScope.Machine)
        {
            inst._mutexName.ShouldEndWith("Global\\" + appId);
        }
    }

    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Machine)]
    [InlineData(InstanceLockScope.Session)]
    [UnixTheory]
    public void Unix_PathGeneration_And_Isolation(InstanceLockScope scope)
    {
        UnixInstanceLockHooks._userIdHook.Value = () => 1001;

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };
        using var inst = new UnixInstanceLock<string>(appId, options, null);

        inst.TryAcquirePrimary().ShouldBeTrue();

        if (scope == InstanceLockScope.User)
        {
            inst._lockFilePath.ShouldContain("user_1001");
        }
        else if (scope == InstanceLockScope.Session)
        {
            inst._lockFilePath.ShouldContain("_session_");
        }
    }

    [UnixFact]
    public void Unix_LongAppId_HashesPipeNameCorrectly()
    {
        var appId = new string('a', 128);
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        using var inst = new UnixInstanceLock<string>(appId, options, null);

        inst._pipeName.Length.ShouldBe(35);
        inst._pipeName.ShouldStartWith("si_");
    }
}
