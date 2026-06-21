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

    [Theory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public void Mutex_UserAndSessionScope_AllowsCurrentSessionUser(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };

        using var instanceLock = new WindowsInstanceLock<string>(appId, options, NullLogger<WindowsInstanceLock<string>>.Instance);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        var mutexName = WindowsInstanceLock<string>.CreateMutexName(appId, scope);
        var mutexOpened = Mutex.TryOpenExisting(mutexName, out var mutex);
        mutexOpened.ShouldBeTrue("The mutex should be accessible to the creator");
        mutex?.Dispose();
    }

    [Theory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public void Mutex_UserAndSessionScope_DisallowsOtherUsers(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = scope };

        using var instanceLock = new WindowsInstanceLock<string>(appId, options, NullLogger<WindowsInstanceLock<string>>.Instance);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        var mutexName = WindowsInstanceLock<string>.CreateMutexName(appId, scope);
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

    [Fact]
    public void Mutex_MachineScope_AllowsAuthenticatedUsers()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.Machine };

        using var instanceLock = new WindowsInstanceLock<string>(appId, options, NullLogger<WindowsInstanceLock<string>>.Instance);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        var mutexName = WindowsInstanceLock<string>.CreateMutexName(appId, InstanceLockScope.Machine);
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

    private static WindowsInstanceLock<string> StartPipeServer(string appId, InstanceLockScope scope)
    {
        var options = new InstanceLockOptions { Scope = scope };
        var instanceLock = new WindowsInstanceLock<string>(appId, options, NullLogger<WindowsInstanceLock<string>>.Instance);
        instanceLock.TryAcquirePrimary().ShouldBeTrue("Failed to acquire primary instance lock");

        using var serverReady = new ManualResetEventSlim(false);
        instanceLock.OnServerReady = () => serverReady.Set();
        _ = instanceLock.RunServerLoop(_ => ValueTask.CompletedTask, null, TestContext.Current.CancellationToken);
        serverReady.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).ShouldBeTrue("Pipe server did not start in time");
        return instanceLock;
    }

    [Theory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public async Task Pipe_UserAndSessionScope_AllowsCurrentSessionUser(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        using var instanceLock = StartPipeServer(appId, scope);

        var pipeName = WindowsInstanceLock<string>.CreatePipeName(appId, scope);
        using var clientPipe = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.Asynchronous);
        await clientPipe.ConnectAsync(2000, TestContext.Current.CancellationToken);
        clientPipe.IsConnected.ShouldBeTrue("The Named Pipe should allow the current user to connect");
    }

    [Theory]
    [InlineData(InstanceLockScope.User)]
    [InlineData(InstanceLockScope.Session)]
    public async Task Pipe_UserAndSessionScope_DisallowsOtherUsers(InstanceLockScope scope)
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        using var instanceLock = StartPipeServer(appId, scope);

        var pipeName = WindowsInstanceLock<string>.CreatePipeName(appId, scope);

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

    [Fact]
    public async Task Pipe_MachineScope_AllowsAuthenticatedUsers()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        using var instanceLock = StartPipeServer(appId, InstanceLockScope.Machine);

        var pipeName = WindowsInstanceLock<string>.CreatePipeName(appId, InstanceLockScope.Machine);

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

    [Fact]
    public void WindowsInstanceLock_ThrowsOnInvalidScope()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var invalidScope = (InstanceLockScope)999;
        var options = new InstanceLockOptions { Scope = invalidScope };

        Should.Throw<NotSupportedException>(() => new WindowsInstanceLock<string>("test", options, null));
        Should.Throw<NotSupportedException>(() => WindowsInstanceLock<string>.CreatePipeName("test", invalidScope));
        Should.Throw<NotSupportedException>(() => WindowsInstanceLock<string>.CreateMutexName("test", invalidScope));
    }

    [Fact]
    public void Pipe_Hijacking_IsPrevented_By_MaxInstances()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var scope = InstanceLockScope.User;

        using var primaryLock = StartPipeServer(appId, scope);
        var pipeName = WindowsInstanceLock<string>.CreatePipeName(appId, scope);

        Should.Throw<Exception>(() =>
        {
            // NamedPipeServerStream constructor throws IOException or UnauthorizedAccessException
            // if maxNumberOfServerInstances constraint is violated or access is denied.
            var hijackPipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.In,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            hijackPipe.Dispose();
        });
    }

    [Fact]
    public void Mutex_TryAcquirePrimary_FallsBackToOpenExisting_WhenCreateFails()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var mutexName = WindowsInstanceLock<string>.CreateMutexName(appId, InstanceLockScope.User);

        using var existingMutex = new Mutex(true, mutexName, out var createdNew);
        createdNew.ShouldBeTrue();

        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        using var instanceLock = new WindowsInstanceLock<string>(appId, options, null);

        instanceLock.TryAcquirePrimary().ShouldBeFalse("TryAcquirePrimary should return false because existingMutex already owns the mutex");
    }

    [Fact]
    public void Mutex_TryAcquirePrimary_HandlesAbandonedMutexException()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var mutexName = WindowsInstanceLock<string>.CreateMutexName(appId, InstanceLockScope.User);

        var thread = new Thread(() => _ = new Mutex(true, mutexName, out _));
        thread.Start();
        thread.Join();

        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        using var instanceLock = new WindowsInstanceLock<string>(appId, options, null);

        instanceLock.TryAcquirePrimary().ShouldBeTrue("TryAcquirePrimary should catch AbandonedMutexException and acquire ownership");
    }

    [Fact]
    public void TryAcquirePrimary_WhenMutexCreationAndOpenFail_ThrowsUnauthorizedAccessException()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        var mutexName = $@"Global\{appId}_user_{GetUserId()}";

        // Create a mutex that denies all access to the current user
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(GetUserId()), MutexRights.FullControl, AccessControlType.Deny));

        using var mutex = MutexAcl.Create(true, mutexName, out var createdNew, security);
        createdNew.ShouldBeTrue();

        using var instanceLock = new WindowsInstanceLock<string>(appId, options, null);
        Should.Throw<UnauthorizedAccessException>(() => instanceLock.TryAcquirePrimary());
    }

    [Fact]
    public void TryAcquirePrimary_WhenPreviousPrimaryCrashedButHandleStillOpen_AcquiresSuccessfully()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Windows only");

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        var mutexName = $@"Global\{appId}_user_{GetUserId()}";

        using var handleAcquiredEvent = new ManualResetEventSlim(false);
        using var canExitEvent = new ManualResetEventSlim(false);

        // Thread 1: Simulate the original primary that acquires the lock and then crashes
        var t = new Thread(() =>
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(new SecurityIdentifier(GetUserId()), MutexRights.FullControl,
                AccessControlType.Allow));

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
        using var instanceLock = new WindowsInstanceLock<string>(appId, options, null);
        var isPrimary = instanceLock.TryAcquirePrimary();
        isPrimary.ShouldBeTrue("InstanceLock should acquire an abandoned mutex even if the object is kept alive by another handle");
    }
}
