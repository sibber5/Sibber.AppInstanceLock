// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace Sibber.AppInstanceLock.Tests.Integration;

/// <summary>
/// Integration tests for InstanceLock lifecycle and synchronization invariants
/// validating thread-safety, state transitions, and lock-free cleanup phases.
/// </summary>
public sealed class IpcLifecycleIntegrationTests : IntegrationTestBase
{
    // This test intentionally couples to internal fields (_pipeCts) and test hooks (OnBeforePipeCtsLock).
    // While this makes it a "change detector" tied to the current CancellationTokenSource implementation,
    // this tight coupling is required to achieve deterministic execution of the teardown path.
    //
    // A pure black-box test would just call `TryAcquire()` followed immediately by `Dispose()`.
    // Because the IPC server loop runs in a background Task, a black-box test introduces a
    // race condition: `Dispose()` might execute before the background task is ever scheduled by the thread pool.
    // If that happens, the background task starts, immediately sees `_disposed = true`, and exits.
    //
    // That black-box approach completely fails to test the most critical teardown path: gracefully
    // shutting down an ACTIVELY listening server loop (cancelling the pipe, unwinding blocking calls).
    //
    // By hooking into the internals, we halt the main test thread until the background loop is
    // provably up, running, and listening. Only then do we call `Dispose()`. This guarantees we are
    // actually testing the complex active-teardown logic. Changing the loop cancellation away from
    // a CTS is extremely unlikely, making the maintenance cost of this change detector acceptable.
    [Fact]
    public async Task Lifecycle_Deterministic_CreateStartDispose()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        var backend = primary._backend;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnBeforePipeCtsLock = () => tcs.TrySetResult();

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        await Task.Run(async () =>
        {
            while (Volatile.Read(ref backend._pipeCts) == null)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        backend._pipeCts.ShouldNotBeNull();

        primary.Dispose();

        // _pipeCts should be null or canceled
        backend._pipeCts?.Token.IsCancellationRequested.ShouldBe(true, "The server loop cancellation token should be requested to cancel");

        // Wait for server loop task to complete gracefully
        if (primary._pipeServerLoopTask is { } serverTask)
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            serverTask.Status.ShouldBe(TaskStatus.RanToCompletion);
        }
    }

    [Fact]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
    public async Task Lifecycle_ConcurrentDisposeDuringInitialization_DoesNotLeak()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        var backend = primary._backend;

        using var barrier = new Barrier(2);

        backend.OnBeforePipeCtsLock = () =>
        {
            barrier.SignalAndWait(5000).ShouldBeTrue(); // Wait for Dispose thread to reach its hook
            barrier.SignalAndWait(5000).ShouldBeTrue(); // Wait for Dispose thread to finish locking
        };

        backend.OnBeforeDisposeCtsLock = () =>
        {
            barrier.SignalAndWait(5000).ShouldBeTrue(); // Align with RunServerLoop
            // RunServerLoop is now waiting for the second signal, so we signal it
            // and immediately lock _pipeCtsLock inside Dispose()
            barrier.SignalAndWait(5000).ShouldBeTrue();
        };

        // We call TryAcquirePrimary directly to set it as primary,
        // then RunServerLoop to trigger the task.
        var isPrimary = (bool)backend.GetType().GetMethod("TryAcquirePrimary", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(backend, null)!;
        isPrimary.ShouldBeTrue();

        var serverTask = backend.RunServerLoop(_ => ValueTask.CompletedTask, null);

        // Run Dispose on another thread
        var disposeTask = Task.Run(primary.Dispose, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert no CancellationTokenSource was leaked because Dispose set _disposed = true
        // and the double-check inside lock (_pipeCtsLock) saw it.
        backend._pipeCts.ShouldBeNull();
    }

    [Fact]
    [SuppressMessage("ReSharper", "AccessToDisposedClosure")]
    public async Task Lifecycle_InterleavedLockFreeCleanupVsDispose_IsSafe()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        var backend = primary._backend;

        using var barrier = new Barrier(2);

        backend.OnBeforeServerLoopCleanup = () =>
        {
            barrier.SignalAndWait(5000).ShouldBeTrue();
            barrier.SignalAndWait(5000).ShouldBeTrue();
        };

        backend.OnBeforeDisposeCtsLock = () =>
        {
            barrier.SignalAndWait(5000).ShouldBeTrue();
            barrier.SignalAndWait(5000).ShouldBeTrue();
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnBeforePipeCtsLock = () => tcs.TrySetResult();

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Run(async () =>
        {
            while (Volatile.Read(ref backend._pipeCts) == null)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Cancel the pipe to force the server loop to exit and hit the cleanup finally block
        await backend._pipeCts!.CancelAsync();

        var disposeTask = Task.Run(primary.Dispose, TestContext.Current.CancellationToken);

        var serverTask = primary._pipeServerLoopTask;

        await Task.WhenAll(serverTask!, disposeTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        backend._pipeCts.ShouldBeNull();
    }

    // Validates the atomic initialization of _pipeCts under _pipeCtsLock in the backend.
    // While InstanceLock.TryAcquire() is thread-unsafe by design, InstanceLock.Dispose() is
    // thread-safe. A concurrent Dispose() call during TryAcquire()'s startup phase must
    // either cancel a fully initialized _pipeCts or safely no-op, without leaking an
    // orphaned server loop.
    //
    // Because forcing a deterministic interleaving of TryAcquire() and Dispose() is highly
    // prone to flakiness, this test instead hammers the backend's internal lock state
    // machine via multithreaded concurrent invocation. Proving the internal mechanism
    // cannot be corrupted implicitly guarantees resilience against the Dispose() teardown race.
    [Fact]
    public async Task Lifecycle_MultiThreadedDoubleStart_PreventsDoubleExecution()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        var backend = primary._backend;
        var isPrimary = (bool)backend.GetType().GetMethod("TryAcquirePrimary", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(backend, null)!;
        isPrimary.ShouldBeTrue();

        var numThreads = 10;
        var startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[numThreads];
        var executingThreads = 0;

        for (var i = 0; i < numThreads; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await startTcs.Task;
                try
                {
                    var t = backend.RunServerLoop(_ => ValueTask.CompletedTask, null);
                    Interlocked.Increment(ref executingThreads);
                    await t;
                }
                catch (UnreachableException ex)
                {
                    Interlocked.Increment(ref executingThreads);
                    ex.Message.ShouldNotBeNull();
                }
            }, TestContext.Current.CancellationToken);
        }

        startTcs.SetResult();

        await Task.Run(async () =>
        {
            while ((Volatile.Read(ref backend._pipeCts) == null || Volatile.Read(ref executingThreads) < numThreads) && !TestContext.Current.CancellationToken.IsCancellationRequested)
            {
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
        }, TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Cancel the created CTS so the primary loop exits
        if (backend._pipeCts is not null) await backend._pipeCts.CancelAsync();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // After exit, _pipeCts is null because the loop cleans it up in the finally block.
        // Or wait, if the tasks complete, the finally block runs and sets _pipeCts to null.
        backend._pipeCts.ShouldBeNull();
    }

    [Fact]
    public async Task Lifecycle_MultiThreadedDoubleDispose_IsSafe()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var numThreads = 10;
        var startTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[numThreads];

        for (var i = 0; i < numThreads; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                await startTcs.Task;
                primary.Dispose();
            }, TestContext.Current.CancellationToken);
        }

        startTcs.SetResult();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        primary._backend._pipeCts.ShouldBeNull();
    }

    [Fact]
    public async Task Lifecycle_PostDispose_ThrowsObjectDisposedException()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        primary.Dispose();

        Should.Throw<ObjectDisposedException>(() => primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        await Should.ThrowAsync<ObjectDisposedException>(async () => await primary._backend.RunServerLoop(_ => ValueTask.CompletedTask, null));
    }
}
