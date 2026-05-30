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

    [Fact]
    public async Task Lifecycle_Deterministic_CreateStartDispose()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        var backend = primary._backend;
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnBeforePipeCtsLock = () => tcs.TrySetResult();

        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        // give it a moment to enter the lock and assign _pipeCts
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.NotNull(backend._pipeCts);

        primary.Dispose();

        // _pipeCts should be null or canceled
        if (backend._pipeCts != null)
        {
            Assert.True(backend._pipeCts.Token.IsCancellationRequested);
        }

        // Wait for server loop task to complete gracefully
        if (primary._pipeServerLoopTask is { } serverTask)
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.True(serverTask.IsCompletedSuccessfully);
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
            barrier.SignalAndWait(5000); // Wait for Dispose thread to reach its hook
            barrier.SignalAndWait(5000); // Wait for Dispose thread to finish locking
        };

        backend.OnBeforeDisposeCtsLock = () =>
        {
            barrier.SignalAndWait(5000); // Align with RunServerLoop
            // RunServerLoop is now waiting for the second signal, so we signal it
            // and immediately lock _pipeCtsLock inside Dispose()
            barrier.SignalAndWait(5000);
        };

        // We call TryAcquirePrimary directly to set it as primary,
        // then RunServerLoop to trigger the task.
        var isPrimary = (bool)backend.GetType().GetMethod("TryAcquirePrimary", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(backend, null)!;
        Assert.True(isPrimary);

        var serverTask = backend.RunServerLoop(_ => ValueTask.CompletedTask, null, CancellationToken.None);

        // Run Dispose on another thread
        var disposeTask = Task.Run(primary.Dispose, TestContext.Current.CancellationToken);

        await Task.WhenAll(serverTask, disposeTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Assert no CancellationTokenSource was leaked because Dispose set _disposed = true
        // and the double-check inside lock (_pipeCtsLock) saw it.
        Assert.Null(backend._pipeCts);
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
            barrier.SignalAndWait(5000);
            barrier.SignalAndWait(5000);
        };

        backend.OnBeforeDisposeCtsLock = () =>
        {
            barrier.SignalAndWait(5000);
            barrier.SignalAndWait(5000);
        };

        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        backend.OnBeforePipeCtsLock = () => tcs.TrySetResult();

        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        // Cancel the pipe to force the server loop to exit and hit the cleanup finally block
        await backend._pipeCts!.CancelAsync();

        var disposeTask = Task.Run(primary.Dispose, TestContext.Current.CancellationToken);

        var serverTask = primary._pipeServerLoopTask;

        await Task.WhenAll(serverTask!, disposeTask).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(backend._pipeCts);
    }

    [Fact]
    public async Task Lifecycle_MultiThreadedDoubleStart_PreventsDoubleExecution()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        var backend = primary._backend;
        var isPrimary = (bool)backend.GetType().GetMethod("TryAcquirePrimary", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.Invoke(backend, null)!;
        Assert.True(isPrimary);

        var numThreads = 10;
        using var startEvent = new ManualResetEventSlim(false);
        var tasks = new Task[numThreads];

        for (var i = 0; i < numThreads; i++)
        {
            tasks[i] = Task.Run(async () =>
            {
                // ReSharper disable once AccessToDisposedClosure
                startEvent.Wait();
                try
                {
                    var task = backend.RunServerLoop(_ => ValueTask.CompletedTask, null, CancellationToken.None);
                    await task;
                }
                catch (UnreachableException) { }
            }, TestContext.Current.CancellationToken);
        }

        startEvent.Set();

        // Wait briefly for all threads to hit the lock and start/abort the loop
        await Task.Delay(500, TestContext.Current.CancellationToken);

        // Cancel the created CTS so the primary loop exits
        if (backend._pipeCts is not null) await backend._pipeCts.CancelAsync();

        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        // After exit, _pipeCts is null because the loop cleans it up in the finally block.
        // Or wait, if the tasks complete, the finally block runs and sets _pipeCts to null.
        Assert.Null(backend._pipeCts);
    }

    [Fact]
    public async Task Lifecycle_MultiThreadedDoubleDispose_IsSafe()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        var numThreads = 10;
        using var startEvent = new ManualResetEventSlim(false);
        var tasks = new Task[numThreads];

        for (var i = 0; i < numThreads; i++)
        {
            tasks[i] = Task.Run(() =>
            {
                // ReSharper disable once AccessToDisposedClosure
                startEvent.Wait();
                primary.Dispose();
            }, TestContext.Current.CancellationToken);
        }

        startEvent.Set();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        Assert.Null(primary._backend._pipeCts);
    }

    [Fact]
    public async Task Lifecycle_PostDispose_ThrowsObjectDisposedException()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(appId, () => "test", _ => ValueTask.CompletedTask);

        primary.Dispose();

        Assert.Throws<ObjectDisposedException>(() => primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        await Assert.ThrowsAsync<ObjectDisposedException>(async () => await primary._backend.RunServerLoop(_ => ValueTask.CompletedTask, null, CancellationToken.None));
    }
}
