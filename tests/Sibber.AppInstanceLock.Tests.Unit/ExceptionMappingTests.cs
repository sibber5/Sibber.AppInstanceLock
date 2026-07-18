// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.AccessControl;
using System.Security.Principal;
using Sibber.AppInstanceLock.Tests.Shared;

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class ExceptionMappingTests : UnitTestBase
{
    [WindowsFact]
    public void Windows_ExceptionMapping_AccessDenied()
    {
        var appId = UniqueAppId();
        var mutexName = @$"Local\{appId}";
        var security = new MutexSecurity();
        using var wid = WindowsIdentity.GetCurrent();
        var rule = new MutexAccessRule(wid.User!, MutexRights.FullControl, AccessControlType.Deny);
        security.AddAccessRule(rule);

        using var m = MutexAcl.Create(false, mutexName, out var _, security);
        var options = new InstanceLockOptions { Scope = InstanceLockScope.Session };
        using var inst = new WindowsInstanceLock<string>(appId, options, null);
        Should.Throw<UnauthorizedAccessException>(() => inst.TryAcquirePrimary());
    }

    [UnixFact]
    public void Unix_ExceptionMapping_AccessDenied()
    {
        // linux runners on GitHub Actions execute as root.
        if (Environment.UserName == "root")
        {
            Assert.Skip("Test is running as root, this bypasses standard Unix file permission restrictions which makes the test pointless.");
        }

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        using var inst = new UnixInstanceLock<string>(appId, options, null);
        var tempPath = inst._lockFilePath;

        var parentDir = Path.GetDirectoryName(tempPath);
        if (!string.IsNullOrEmpty(parentDir))
        {
            Directory.CreateDirectory(parentDir);
        }

        File.WriteAllText(tempPath, "lock");
        File.SetUnixFileMode(tempPath, UnixFileMode.None); // no access

        try
        {
            Should.Throw<UnauthorizedAccessException>(() => inst.TryAcquirePrimary());
        }
        finally
        {
            try
            {
                File.SetUnixFileMode(tempPath, UnixFileMode.UserWrite | UnixFileMode.UserRead);
                File.Delete(tempPath);
            }
            catch
            {
                // Ignore cleanup failure to not mask the main failure
            }
        }
    }
}
