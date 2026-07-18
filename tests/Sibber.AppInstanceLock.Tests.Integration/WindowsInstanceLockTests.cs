// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Extensions.Logging.Abstractions;
using Sibber.AppInstanceLock.Tests.Shared;

namespace Sibber.AppInstanceLock.Tests.Integration;

[Collection("StaticHooks")]
[SupportedOSPlatform("windows")]
public sealed class WindowsInstanceLockTests : IntegrationTestBase
{
    private static string GetUserId()
    {
        using var id = WindowsIdentity.GetCurrent();
        return id.User!.Value;
    }

    [WindowsTheory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public void Mutex_UserAndSessionScope_AllowsCurrentSessionUser(InstanceLockScope scope)
    {
        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };

        using var instanceLock = new WindowsInstanceLock<byte>(appId, options, NullLogger<WindowsInstanceLock<byte>>.Instance, null);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        var mutexName = WindowsInstanceLock<byte>.CreateMutexName(appId, scope);
        var mutexOpened = Mutex.TryOpenExisting(mutexName, out var mutex);
        mutexOpened.ShouldBeTrue("The mutex should be accessible to the creator");
        mutex?.Dispose();
    }

    [WindowsTheory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public void Mutex_UserAndSessionScope_DisallowsOtherUsers(InstanceLockScope scope)
    {
        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };

        using var instanceLock = new WindowsInstanceLock<byte>(appId, options, NullLogger<WindowsInstanceLock<byte>>.Instance, null);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        var mutexName = WindowsInstanceLock<byte>.CreateMutexName(appId, scope);
        var mutexOpened = Mutex.TryOpenExisting(mutexName, out var mutex);
        mutexOpened.ShouldBeTrue();

        using (mutex)
        {
            var mutexSecurity = mutex!.GetAccessControl();
            var rules = mutexSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));

            var expectedUserSid = new SecurityIdentifier(GetUserId());
            var authUserSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
            var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            var foundAdmin = false;
            var foundSystem = false;

            foreach (MutexAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow)
                {
                    rule.IdentityReference.Value.ShouldNotBe(authUserSid.Value, "Authenticated users should NOT have access in User/Session scope");
                    rule.IdentityReference.Value.ShouldNotBe(worldSid.Value, "Everyone (World) should not have access");
                    rule.IdentityReference.Value.ShouldNotBe(networkSid.Value, "Network should not have access");

                    if (rule.IdentityReference.Value == adminSid.Value) { rule.MutexRights.HasFlag(MutexRights.FullControl).ShouldBeTrue(); foundAdmin = true; }
                    if (rule.IdentityReference.Value == systemSid.Value) { rule.MutexRights.HasFlag(MutexRights.FullControl).ShouldBeTrue(); foundSystem = true; }
                }
            }

            foundAdmin.ShouldBeTrue("Administrators should have FullControl");
            foundSystem.ShouldBeTrue("LocalSystem should have FullControl");
        }
    }

    [WindowsFact]
    public void Mutex_MachineScope_AllowsAuthenticatedUsers()
    {
        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.Machine };

        using var instanceLock = new WindowsInstanceLock<byte>(appId, options, NullLogger<WindowsInstanceLock<byte>>.Instance, null);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        var mutexName = WindowsInstanceLock<byte>.CreateMutexName(appId, InstanceLockScope.Machine);
        var mutexOpened = Mutex.TryOpenExisting(mutexName, out var mutex);
        mutexOpened.ShouldBeTrue();

        using (mutex)
        {
            var mutexSecurity = mutex!.GetAccessControl();
            var rules = mutexSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));

            var authUserSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

            var foundAuthUser = false;
            var foundAdmin = false;
            var foundSystem = false;

            foreach (MutexAccessRule rule in rules)
            {
                if (rule.AccessControlType == AccessControlType.Allow)
                {
                    if (rule.IdentityReference.Value == authUserSid.Value)
                    {
                        rule.MutexRights.HasFlag(MutexRights.Synchronize | MutexRights.Modify).ShouldBeTrue();
                        foundAuthUser = true;
                    }
                    if (rule.IdentityReference.Value == adminSid.Value) { rule.MutexRights.HasFlag(MutexRights.FullControl).ShouldBeTrue(); foundAdmin = true; }
                    if (rule.IdentityReference.Value == systemSid.Value) { rule.MutexRights.HasFlag(MutexRights.FullControl).ShouldBeTrue(); foundSystem = true; }
                }
            }

            foundAuthUser.ShouldBeTrue("Authenticated users should have access in Machine scope");
            foundAdmin.ShouldBeTrue("Administrators should have FullControl in Machine scope");
            foundSystem.ShouldBeTrue("LocalSystem should have FullControl in Machine scope");
        }
    }

    private static WindowsInstanceLock<byte> StartPipeServer(string appId, InstanceLockScope scope)
    {
        var options = new InstanceLockOptions { Scope = scope };
        var instanceLock = new WindowsInstanceLock<byte>(appId, options, NullLogger<WindowsInstanceLock<byte>>.Instance, null);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        using var serverReady = new ManualResetEventSlim(false);
        instanceLock.OnServerReady = () => serverReady.Set();
        _ = instanceLock.RunServerLoop(_ => ValueTask.CompletedTask, null);
        serverReady.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue("Pipe server did not start in time");
        return instanceLock;
    }

    [WindowsTheory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public async Task Pipe_UserAndSessionScope_AllowsCurrentSessionUser(InstanceLockScope scope)
    {
        var appId = UniqueAppId();
        using var instanceLock = StartPipeServer(appId, scope);

        var pipeName = WindowsInstanceLock<byte>.CreatePipeName(appId, scope);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(2000, TestContext.Current.CancellationToken);
        clientPipe.IsConnected.ShouldBeTrue("The Named Pipe should allow the current user to connect");
    }

    [WindowsTheory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public async Task Pipe_UserAndSessionScope_DisallowsOtherUsers(InstanceLockScope scope)
    {
        var appId = UniqueAppId();
        using var instanceLock = StartPipeServer(appId, scope);

        var pipeName = WindowsInstanceLock<byte>.CreatePipeName(appId, scope);

        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(2000, TestContext.Current.CancellationToken);

        var pipeSecurity = clientPipe.GetAccessControl();
        var rules = pipeSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));

        var authUserSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var worldSid = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        var networkSid = new SecurityIdentifier(WellKnownSidType.NetworkSid, null);
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var foundAdmin = false;
        var foundSystem = false;

        foreach (PipeAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow)
            {
                rule.IdentityReference.Value.ShouldNotBe(authUserSid.Value, "Authenticated users should NOT have access in User/Session scope");
                rule.IdentityReference.Value.ShouldNotBe(worldSid.Value, "Everyone (World) should not have access");
                rule.IdentityReference.Value.ShouldNotBe(networkSid.Value, "Network should not have access");

                if (rule.IdentityReference.Value == adminSid.Value) { rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl).ShouldBeTrue(); foundAdmin = true; }
                if (rule.IdentityReference.Value == systemSid.Value) { rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl).ShouldBeTrue(); foundSystem = true; }
            }
        }

        foundAdmin.ShouldBeTrue("Administrators should have FullControl");
        foundSystem.ShouldBeTrue("LocalSystem should have FullControl");
    }

    [WindowsFact]
    public async Task Pipe_MachineScope_AllowsAuthenticatedUsers()
    {
        var appId = UniqueAppId();
        using var instanceLock = StartPipeServer(appId, InstanceLockScope.Machine);

        var pipeName = WindowsInstanceLock<byte>.CreatePipeName(appId, InstanceLockScope.Machine);

        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(2000, TestContext.Current.CancellationToken);

        var pipeSecurity = clientPipe.GetAccessControl();
        var rules = pipeSecurity.GetAccessRules(true, true, typeof(SecurityIdentifier));

        var authUserSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        var adminSid = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var systemSid = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);

        var foundAuthUser = false;
        var foundAdmin = false;
        var foundSystem = false;

        foreach (PipeAccessRule rule in rules)
        {
            if (rule.AccessControlType == AccessControlType.Allow)
            {
                if (rule.IdentityReference.Value == authUserSid.Value)
                {
                    rule.PipeAccessRights.HasFlag(PipeAccessRights.ReadWrite).ShouldBeTrue();
                    foundAuthUser = true;
                }
                if (rule.IdentityReference.Value == adminSid.Value) { rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl).ShouldBeTrue(); foundAdmin = true; }
                if (rule.IdentityReference.Value == systemSid.Value) { rule.PipeAccessRights.HasFlag(PipeAccessRights.FullControl).ShouldBeTrue(); foundSystem = true; }
            }
        }

        foundAuthUser.ShouldBeTrue("Authenticated users should have access in Machine scope");
        foundAdmin.ShouldBeTrue("Administrators should have FullControl in Machine scope");
        foundSystem.ShouldBeTrue("LocalSystem should have FullControl in Machine scope");
    }

    [WindowsFact]
    public void WindowsInstanceLock_ThrowsOnInvalidScope()
    {
        var invalidScope = (InstanceLockScope)999;
        var options = new InstanceLockOptions { Scope = invalidScope };

        Should.Throw<NotSupportedException>(() => new WindowsInstanceLock<byte>("test", options, null, null));
        Should.Throw<NotSupportedException>(() => WindowsInstanceLock<byte>.CreatePipeName("test", invalidScope));
        Should.Throw<NotSupportedException>(() => WindowsInstanceLock<byte>.CreateMutexName("test", invalidScope));
    }

    [WindowsFact]
    public void Pipe_Hijacking_IsPrevented_By_MaxInstances()
    {
        var appId = UniqueAppId();
        var scope = InstanceLockScope.User;

        using var primaryLock = StartPipeServer(appId, scope);
        var pipeName = WindowsInstanceLock<byte>.CreatePipeName(appId, scope);

        Should.Throw<Exception>(() =>
        {
            // NamedPipeServerStream constructor throws IOException or UnauthorizedAccessException
            // if maxNumberOfServerInstances constraint is violated or access is denied.
            var hijackPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous
            );
            hijackPipe.Dispose();
        });
    }

    [WindowsFact]
    public void Mutex_TryAcquirePrimary_FallsBackToOpenExisting_WhenCreateFails()
    {
        var appId = UniqueAppId();
        var mutexName = WindowsInstanceLock<byte>.CreateMutexName(appId, InstanceLockScope.User);

        using var existingMutex = new Mutex(true, mutexName, out var createdNew);
        createdNew.ShouldBeTrue();

        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        using var instanceLock = new WindowsInstanceLock<byte>(appId, options, null, null);

        instanceLock.TryAcquirePrimary().ShouldBeFalse("TryAcquirePrimary should return false because existingMutex already owns the mutex");
    }

    [WindowsFact]
    public void Mutex_TryAcquirePrimary_HandlesAbandonedMutexException()
    {
        var appId = UniqueAppId();
        var mutexName = WindowsInstanceLock<byte>.CreateMutexName(appId, InstanceLockScope.User);

        var thread = new Thread(() => _ = new Mutex(true, mutexName, out _));
        thread.Start();
        thread.Join();

        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        using var instanceLock = new WindowsInstanceLock<byte>(appId, options, null, null);

        instanceLock.TryAcquirePrimary().ShouldBeTrue("TryAcquirePrimary should catch AbandonedMutexException and acquire ownership");
    }

    [WindowsFact]
    public void TryAcquirePrimary_WhenMutexCreationAndOpenFail_ThrowsUnauthorizedAccessException()
    {
        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        var mutexName = $@"Global\{appId}_user_{GetUserId()}";

        // Create a mutex that denies all access to the current user
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(GetUserId()), MutexRights.FullControl, AccessControlType.Deny));

        using var mutex = MutexAcl.Create(true, mutexName, out var createdNew, security);
        createdNew.ShouldBeTrue();

        using var instanceLock = new WindowsInstanceLock<byte>(appId, options, null, null);
        Should.Throw<UnauthorizedAccessException>(() => instanceLock.TryAcquirePrimary());
    }

    [WindowsFact]
    public void TryAcquirePrimary_WhenPreviousPrimaryCrashedButHandleStillOpen_AcquiresSuccessfully()
    {
        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        var mutexName = $@"Global\{appId}_user_{GetUserId()}";

        using var handleAcquiredEvent = new ManualResetEventSlim(false);
        using var canExitEvent = new ManualResetEventSlim(false);

        // Thread 1: Simulate the original primary that acquires the lock and then crashes
        var t = new Thread(() =>
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(GetUserId()), MutexRights.FullControl, AccessControlType.Allow));

            // Create and acquire ownership
            var mutex = MutexAcl.Create(true, mutexName, out _, security);

            handleAcquiredEvent.Set();
            canExitEvent.Wait(TestContext.Current.CancellationToken);

            // Exit without releasing, abandoning the mutex
            // Deliberately NOT disposing or releasing the mutex
        });
        t.Start();

        // Wait for Thread 1 to create and acquire the mutex
        handleAcquiredEvent.Wait(TestContext.Current.CancellationToken);

        // Main Thread: Simulate a secondary instance that holds a handle, keeping the NT kernel object alive
        using var secondaryMutex = Mutex.OpenExisting(mutexName);

        // Tell Thread 1 to "crash" (exit)
        canExitEvent.Set();
        t.Join();

        // Main Thread: Simulate a new instance starting up
        using var instanceLock = new WindowsInstanceLock<byte>(appId, options, null, null);
        var isPrimary = instanceLock.TryAcquirePrimary();
        isPrimary.ShouldBeTrue("InstanceLock should acquire an abandoned mutex even if the object is kept alive by another handle");
    }
}
