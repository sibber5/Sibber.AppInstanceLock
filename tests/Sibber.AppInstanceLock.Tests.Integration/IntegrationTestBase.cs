// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Sibber.AppInstanceLock.Tests.Shared;

namespace Sibber.AppInstanceLock.Tests.Integration;

public abstract class IntegrationTestBase : TestBase
{
    protected override string Prefix => "integ";
}

// ReSharper disable GrammarMistakeInComment
// ============================================================================
// EXCLUDED TESTS — DOCUMENTATION BLOCK
// ============================================================================
//
// The following tests are explicitly EXCLUDED from this suite because they
// require multi-process, multi-user, or container-level isolation that cannot
// be safely simulated within a single xUnit test runner process.
//
// ┌────────────────────────────────────────────────────────────────────────┐
// │ EXCLUDED TEST                         │ REASON                         │
// ├────────────────────────────────────────────────────────────────────────┤
// │ Cross-Process Mutex/Flock Abandonment │ Requires SIGKILL/FailFast of a │
// │                                       │ child process and verifying a  │
// │                                       │ second process can reclaim the │
// │                                       │ abandoned OS lock. Cannot be   │
// │                                       │ done in-process.               │
// │                                       │                                │
// │ Cross-User / Cross-Session Isolation  │ Requires spawning processes    │
// │                                       │ under different OS user        │
// │                                       │ accounts or terminal sessions. │
// │                                       │ Needs runas/su + container     │
// │                                       │ orchestration.                 │
// │                                       │                                │
// │ Security & ACL Verification           │ Requires verifying that an     │
// │                                       │ unauthorized process cannot    │
// │                                       │ connect to the Named Pipe or   │
// │                                       │ tamper with the lock file.     │
// │                                       │ Needs multi-user + permission  │
// │                                       │ boundary testing.              │
// │                                       │                                │
// │ Server Retry Policy: Exponential      │ The retry policy's backoff     │
// │ Backoff Timing Verification           │ timing depends on real elapsed │
// │                                       │ time inside RunServerLoop.     │
// │                                       │ Forcing pipe failures requires │
// │                                       │ injecting a mock pipe factory  │
// │                                       │ (CreatePipeServer is abstract  │
// │                                       │ but sealed behind internal     │
// │                                       │ class hierarchy).              │
// └────────────────────────────────────────────────────────────────────────┘
//
// ARCHITECTURAL REFACTORING ROADMAP (to enable excluded tests):
//
// 1. Static Identity & Session Resolution
//    - WindowsInstanceLock uses WindowsIdentity.GetCurrent() / Process.SessionId.
//    - UnixInstanceLock uses PInvoke.getuid() / SessionGetInfo.
//    → Introduce IUserIdentityResolver and ISessionIdResolver.
//    → Inject into backend constructors to allow deterministic test identities.
//
// 2. Hardcoded OS Resource Paths
//    - Lock file paths (~/.local/share, /var/lock) and Mutex namespaces (Global\)
//      are computed inline.
//    → Introduce IFileSystem (e.g. System.IO.Abstractions) for Unix lock paths.
//    → Accept a configurable Mutex name prefix for Windows test isolation.
//
// 3. Direct OS Handle Management
//    - TryAcquirePrimary() directly instantiates Mutex and calls flock().
//    - CreatePipeServer() is abstract but InstanceLockImpl is internal.
//    → Extract locking into IOSLockPrimitive (WindowsMutexPrimitive,
//      UnixFlockPrimitive). Tests can inject a mock that simulates:
//        - Handle exhaustion
//        - EACCES / SecurityException
//        - Ghost-held locks (lock held but no live process)
//    → Make CreatePipeServer() injectable or expose a factory interface
//      so tests can supply a pipe that throws on demand to verify the
//      exponential backoff timing in RunServerLoop.
//
// 4. Multi-Process Test Harness
//    → Build a small helper console app that can be spawned as a child
//      process by the test runner. The child acquires the lock, writes
//      to stdout, and the parent verifies exclusivity before/after kill.
//    → Use containers (testcontainers-dotnet) for cross-user/cross-session
//      scenarios on CI.
// ============================================================================
// ReSharper restore GrammarMistakeInComment
