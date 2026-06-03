// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class PathGenerationTests : UnitTestBase
{
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Machine)]
    [InlineData(InstanceLockScope.Session)]
    [Theory]
    public void Windows_PathGeneration_And_Isolation(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsWindows()) return;

        WindowsInstanceLock<string>._userIdHook = () => "S-1-5-21-1234567890-1234567890-1234567890-1001";
        WindowsInstanceLock<string>._sessionIdHook = () => 2;

        try
        {
            var appId = UniqueAppId();
            var options = new InstanceLockOptions { Scope = scope };
            using var inst = new WindowsInstanceLock<string>(appId, options, null);

            // Just verifying it can be constructed without exception with the mocked identity.
            // The paths are private, so we'll test the effects by ensuring TryAcquire throws no unexpected exceptions.
            inst.TryAcquirePrimary().ShouldBeTrue();
        }
        finally
        {
            WindowsInstanceLock<string>._userIdHook = null;
            WindowsInstanceLock<string>._sessionIdHook = null;
        }
    }

    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    [Theory]
    public void Unix_PathGeneration_And_Isolation(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;

        UnixInstanceLock<string>._userIdHook = () => 1001;
        UnixInstanceLock<string>._sessionIdHook = () => "2";

        try
        {
            var appId = UniqueAppId();
            var options = new InstanceLockOptions { Scope = scope };
            using var inst = new UnixInstanceLock<string>(appId, options, null);

            // Just verifying it can be constructed without exception with the mocked identity
            inst.TryAcquirePrimary().ShouldBeTrue();
        }
        finally
        {
            UnixInstanceLock<string>._userIdHook = null;
            UnixInstanceLock<string>._sessionIdHook = null;
        }
    }
}
