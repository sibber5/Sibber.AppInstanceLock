// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class ExceptionMappingTests : UnitTestBase
{
    [Fact]
    public void CrossPlatformExceptionMapping_AccessDenied()
    {
        if (OperatingSystem.IsLinux() && Environment.UserName == "root")
        {
            // GitHub Actions CI Note: Linux runners execute as root, bypassing standard Unix file permission restrictions.
            return;
        }

        var appId = UniqueAppId();

        if (OperatingSystem.IsWindows())
        {
            var mutexName = @$"Local\{appId}";
            var security = new System.Security.AccessControl.MutexSecurity();
            var wid = System.Security.Principal.WindowsIdentity.GetCurrent();
            var rule = new System.Security.AccessControl.MutexAccessRule(wid.User!, System.Security.AccessControl.MutexRights.FullControl, System.Security.AccessControl.AccessControlType.Deny);
            security.AddAccessRule(rule);

            using var m = MutexAcl.Create(false, mutexName, out var _, security);
            var options = new InstanceLockOptions { Scope = InstanceLockScope.Session };
            using var inst = new WindowsInstanceLock<string>(appId, options, null);
            inst.TryAcquirePrimary().ShouldBeFalse();
        }
        else if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
        {
            var options = new InstanceLockOptions { Scope = InstanceLockScope.Session };
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
                inst.TryAcquirePrimary().ShouldBeFalse();
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
}
