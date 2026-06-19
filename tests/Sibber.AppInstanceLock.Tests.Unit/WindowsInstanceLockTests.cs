// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Sibber.AppInstanceLock.Tests.Unit;

[Collection("StaticHooks")]
[SupportedOSPlatform("windows")]
public sealed class WindowsInstanceLockTests : UnitTestBase
{
    [Theory]
    [InlineData(InstanceLockScope.Machine)]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public void CreateMutexSecurity_AppliesCorrectAccessRules(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsWindows()) return;

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };
        using var instanceLock = new WindowsInstanceLock<string>(appId, options, null);

        var security = instanceLock.CreateMutexSecurity();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        var hasAdmin = false;
        var hasSystem = false;
        var hasUser = false;

        var expectedUserSid = scope == InstanceLockScope.Machine
            ? new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)
            : WindowsIdentity.GetCurrent().User!;

        foreach (MutexAccessRule rule in rules)
        {
            if (rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value)
            {
                rule.MutexRights.HasFlag(MutexRights.FullControl).ShouldBeTrue();
                hasAdmin = true;
            }
            else if (rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value)
            {
                rule.MutexRights.HasFlag(MutexRights.FullControl).ShouldBeTrue();
                hasSystem = true;
            }
            else if (rule.IdentityReference.Value == expectedUserSid.Value)
            {
                rule.MutexRights.HasFlag(MutexRights.Synchronize).ShouldBeTrue();
                hasUser = true;
            }
        }

        hasAdmin.ShouldBeTrue();
        hasSystem.ShouldBeTrue();
        hasUser.ShouldBeTrue();
    }

    [Theory]
    [InlineData(InstanceLockScope.Machine)]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public void CreatePipeServer_AppliesCorrectAccessRules(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsWindows()) return;

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };
        using var instanceLock = new WindowsInstanceLock<string>(appId, options, null);

        var method = typeof(WindowsInstanceLock<string>).GetMethod("CreatePipeServer", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        method.ShouldNotBeNull();

        using var pipeServer = (NamedPipeServerStream)method.Invoke(instanceLock, null)!;

        var security = pipeServer.GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier));

        var hasAdmin = false;
        var hasSystem = false;
        var hasUser = false;

        var expectedUserSid = scope == InstanceLockScope.Machine
            ? new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)
            : WindowsIdentity.GetCurrent().User!;

        foreach (PipeAccessRule rule in rules)
        {
            if (rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value)
            {
                rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl).ShouldBeTrue();
                hasAdmin = true;
            }
            else if (rule.IdentityReference.Value == new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value)
            {
                rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl).ShouldBeTrue();
                hasSystem = true;
            }
            else if (rule.IdentityReference.Value == expectedUserSid.Value)
            {
                rule.PipeAccessRights.ShouldBe(PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize);
                hasUser = true;
            }
        }

        hasAdmin.ShouldBeTrue();
        hasSystem.ShouldBeTrue();
        hasUser.ShouldBeTrue();
    }
}
