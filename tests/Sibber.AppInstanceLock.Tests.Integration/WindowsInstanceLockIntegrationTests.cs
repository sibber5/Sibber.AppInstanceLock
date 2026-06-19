// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Sibber.AppInstanceLock.Tests.Integration;

[Collection("StaticHooks")]
[SupportedOSPlatform("windows")]
public sealed class WindowsInstanceLockIntegrationTests : IntegrationTestBase
{
    [Fact]
    public void TryAcquirePrimary_WhenMutexCreationAndOpenFail_ReturnsFalse()
    {
        if (!OperatingSystem.IsWindows()) return;

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        var mutexName = $@"Global\{appId}_user_{WindowsIdentity.GetCurrent().User!.Value}";

        // Create a mutex that denies all access to the current user
        var security = new MutexSecurity();
        security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl, AccessControlType.Deny));

        using var mutex = MutexAcl.Create(true, mutexName, out var createdNew, security);
        createdNew.ShouldBeTrue();

        using var instanceLock = new WindowsInstanceLock<string>(appId, options, null);
        var isPrimary = instanceLock.TryAcquirePrimary();

        isPrimary.ShouldBeFalse();
    }

    [Fact]
    public void TryAcquirePrimary_WhenPreviousPrimaryCrashedButHandleStillOpen_AcquiresSuccessfully()
    {
        if (!OperatingSystem.IsWindows()) return;

        var appId = UniqueAppId();
        var options = new InstanceLockOptions { Scope = InstanceLockScope.User };
        var mutexName = $@"Global\{appId}_user_{WindowsIdentity.GetCurrent().User!.Value}";

        using var handleAcquiredEvent = new ManualResetEventSlim(false);
        using var canExitEvent = new ManualResetEventSlim(false);

        // Thread 1: Simulate the original primary that acquires the lock and then crashes
        var t = new Thread(() =>
        {
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(WindowsIdentity.GetCurrent().User!, MutexRights.FullControl,
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
