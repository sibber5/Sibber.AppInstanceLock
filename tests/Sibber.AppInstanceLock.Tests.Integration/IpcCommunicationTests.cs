// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.IO.Pipes;

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class IpcCommunicationTests : IntegrationTestBase
{
    /// <summary>
    /// Awaits the internal _pipeServerLoopTask. Used solely for test synchronization.
    /// </summary>
    private static async Task AwaitServerLoopAsync(InstanceLock<string> instance, TimeSpan timeout)
    {
        var task = instance._pipeServerLoopTask;
        if (task is null) return;
        await task.WaitAsync(timeout, TestContext.Current.CancellationToken);
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: IPC Message Framing ─ Complex Type (JSON length-prefixed)
    // "Secondary instances transmit data using a 4-byte LE length + UTF-8 JSON payload."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IpcRoundTrip_StringMessage_ReceivedByPrimary()
    {
        var appId = UniqueAppId();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = "hello-from-secondary";

        var primary = CreateLock(
            appId,
            createMsg: () => expected,
            onOtherInstance: msg =>
            {
                tcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            }
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var secondary = CreateLock(
            appId,
            createMsg: () => expected,
            onOtherInstance: _ => ValueTask.CompletedTask
        );

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        received.ShouldBe(expected);
    }

    [Fact]
    public async Task IpcRoundTrip_ZeroLengthMessage_ReceivedByPrimary()
    {
        var appId = UniqueAppId();
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        var primary = CreateLock(
            appId,
            createMsg: () => "test",
            onOtherInstance: msg =>
            {
                tcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            }
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var pipeName = primary._backend._pipeName;
        await using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.Out, PipeOptions.None);
        await client.ConnectAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // Write 4 bytes of 0 to indicate a length of 0
        var lenBuf = new byte[4];
        await client.WriteAsync(lenBuf, TestContext.Current.CancellationToken);
        await client.FlushAsync(TestContext.Current.CancellationToken);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        received.ShouldBeNull();
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: IPC Message Framing ─ Single-byte type (raw byte)
    // "For 1-byte value types: Raw 1-byte transmission."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IpcRoundTrip_ByteMessage_ReceivedByPrimary()
    {
        var appId = UniqueAppId();
        var tcs = new TaskCompletionSource<byte>(TaskCreationOptions.RunContinuationsAsynchronously);
        byte expected = 0xAB;

        var primary = CreateLock(
            appId,
            createMsg: () => expected,
            onOtherInstance: msg =>
            {
                tcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            }
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var secondary = new InstanceLock<byte>(
            appId,
            createMsgToPrimary: () => expected,
            onOtherInstanceOpened: _ => ValueTask.CompletedTask
        );
        _disposables.Add(secondary);

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        received.ShouldBe(expected);
    }

    [Fact]
    public async Task IpcRoundTrip_BoolMessage_ReceivedByPrimary()
    {
        var appId = UniqueAppId();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var primary = new InstanceLock<bool>(
            appId,
            createMsgToPrimary: () => true,
            onOtherInstanceOpened: msg =>
            {
                tcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            }
        );
        _disposables.Add(primary);

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var secondary = new InstanceLock<bool>(
            appId,
            createMsgToPrimary: () => true,
            onOtherInstanceOpened: _ => ValueTask.CompletedTask
        );
        _disposables.Add(secondary);

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        received.ShouldBeTrue();
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Multiple IPC messages across multiple secondaries
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task IpcRoundTrip_MultipleSecondaries_AllMessagesReceived()
    {
        var appId = UniqueAppId();
        var received = new List<string>();
        var allReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        const int Count = 5;

        var messageTcsList = Enumerable.Range(0, Count)
            .Select(_ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToList();

        var primary = CreateLock(
            appId,
            createMsg: () => "x",
            onOtherInstance: msg =>
            {
                if (msg.StartsWith("msg-", StringComparison.Ordinal) && int.TryParse(msg.AsSpan(4), out var index))
                {
                    messageTcsList[index].TrySetResult();
                }
                lock (received)
                {
                    received.Add(msg);
                    if (received.Count == Count) allReceived.TrySetResult();
                }
                return ValueTask.CompletedTask;
            }
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        for (var i = 0; i < Count; i++)
        {
            var idx = i;
            var secondary = CreateLock(
                appId,
                createMsg: () => $"msg-{idx}",
                onOtherInstance: _ => ValueTask.CompletedTask
            );
            secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();
            secondary.Dispose();

            // Deterministic wait: wait until the primary has received this message
            await messageTcsList[i].Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        }

        await allReceived.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        received.Count.ShouldBe(Count);
        for (var i = 0; i < Count; i++)
        {
            received.ShouldContain($"msg-{i}");
        }
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Notification Handshake Resilience
    // "Secondary instances block and retry sending their message."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task NotificationRetry_SecondaryRetriesUntilPrimaryAccepts()
    {
        var appId = UniqueAppId();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new InstanceLockOptions
        {
            // generous retry window so the secondary has enough attempts
            NotificationRetryPolicy = new NotificationRetryPolicy(
                RetryAttempts: 20,
                MaxJitterDelay: TimeSpan.FromMilliseconds(150),
                ConnectionTimeout: TimeSpan.FromMilliseconds(500)
            ),
        };

        var primary = CreateLock(
            appId,
            createMsg: () => "msg",
            onOtherInstance: msg =>
            {
                tcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            },
            options: options
        );

        // Primary acquires lock and starts server.
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        // Secondary runs concurrently ─ the pipe server is already started, but the
        // notification retry policy handles the inherent race between
        // WaitForConnectionAsync and the client's Connect call.
        var secondaryTask = Task.Run(() =>
        {
            var secondary = CreateLock(
                appId,
                createMsg: () => "retry-delivery",
                onOtherInstance: _ => ValueTask.CompletedTask,
                options: options
            );
            secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken);
        }, TestContext.Current.CancellationToken);

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        received.ShouldBe("retry-delivery");

        await secondaryTask.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Server Loop Resilience ─ onMessage exceptions are swallowed
    // "Handler exceptions (onMessage) are caught, logged, and ignored."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ServerLoop_OnMessageThrows_ServerContinuesAcceptingMessages()
    {
        var appId = UniqueAppId();
        var callCount = 0;
        var secondMessageTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

        var firstMessageTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var primary = CreateLock(
            appId,
            createMsg: () => "ping",
            onOtherInstance: msg =>
            {
                var n = Interlocked.Increment(ref callCount);
                if (n == 1)
                {
                    firstMessageTcs.TrySetResult();
                    throw new InvalidOperationException("Intentional test explosion");
                }
                secondMessageTcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            }
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        // first secondary triggers the throw
        var s1 = CreateLock(appId, () => "first", _ => ValueTask.CompletedTask);
        s1.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();
        s1.Dispose();

        // Wait for the primary to process the first message and throw
        await firstMessageTcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        // second secondary should still be handled
        var s2 = CreateLock(appId, () => "second", _ => ValueTask.CompletedTask);
        s2.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();

        var received = await secondMessageTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        received.ShouldBe("second");
        callCount.ShouldBeGreaterThanOrEqualTo(2);
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Server Loop Resilience ─ onServerException controls retry
    // "If onServerException returns false, the server will terminate."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ServerLoop_SingleByteTimeout_BrutalDisposeRecovers()
    {
        var appId = UniqueAppId();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var primary = new InstanceLock<bool>(
            appId,
            createMsgToPrimary: () => true,
            onOtherInstanceOpened: msg =>
            {
                tcs.TrySetResult(msg);
                return ValueTask.CompletedTask;
            }
        );
        _disposables.Add(primary);

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var pipeName = primary._backend._pipeName;

        // 1. Malicious or broken client connects but sends NOTHING.
        await using (var brokenClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
        {
            await brokenClient.ConnectAsync(3000, TestContext.Current.CancellationToken);

            // Wait for 4 seconds to trigger the 3-second timeout inside the primary server loop.
            // This forces the "brutal dispose" path on the single-byte fast path.
            await Task.Delay(TimeSpan.FromSeconds(4), TestContext.Current.CancellationToken);
        }

        // 2. The primary server should have cleanly caught the ObjectDisposedException, broken out, and rebuilt the pipe.
        // A legitimate secondary should now be able to connect and successfully notify the primary.
        var secondary = new InstanceLock<bool>(
            appId,
            createMsgToPrimary: () => true,
            onOtherInstanceOpened: _ => ValueTask.CompletedTask
        );
        _disposables.Add(secondary);

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();

        var received = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        received.ShouldBeTrue();
    }

    [Fact]
    public async Task ServerLoop_OnServerExceptionReturnsFalse_ServerTerminates()
    {
        var appId = UniqueAppId();
        var exceptionSeen = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);

        var options = new InstanceLockOptions
        {
            InstanceServerRetryPolicy = InstanceServerRetryPolicy.DontRetry,
        };

        var primary = CreateLock(
            appId,
            createMsg: () => "x",
            onOtherInstance: _ => ValueTask.CompletedTask,
            onServerException: ex =>
            {
                exceptionSeen.TrySetResult(ex);
                return false; // terminate
            },
            options: options
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        // Trigger a server-side exception by connecting and sending a malformed length header
        // that indicates a message larger than 1 MiB, causing InvalidOperationException.
        var pipeName = primary._backend._pipeName;
        await using (var maliciousClient = new NamedPipeClientStream(".", pipeName, PipeDirection.Out))
        {
            await maliciousClient.ConnectAsync(5000, TestContext.Current.CancellationToken);
            // write a 4-byte LE int with value > 1 MiB to trigger the size guard
            var buf = new byte[4];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(buf, 2 * 1024 * 1024);
            await maliciousClient.WriteAsync(buf, TestContext.Current.CancellationToken);
            await maliciousClient.FlushAsync(TestContext.Current.CancellationToken);
        }

        // onServerException should have been called
        var ex2 = await exceptionSeen.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        ex2.ShouldBeOfType<InvalidOperationException>();

        // The server loop should have completed (no fault on the returned task).
        await AwaitServerLoopAsync(primary, TimeSpan.FromSeconds(5));

        var serverTask = primary._pipeServerLoopTask;
        serverTask?.Status.ShouldBe(TaskStatus.RanToCompletion);
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Notification with no IPC configured is a no-op
    // "If createMsgToPrimary and onOtherInstanceOpened are null, no IPC occurs."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void TryAcquireOrNotify_WithoutIpcCallbacks_SecondaryDoesNotThrow()
    {
        var appId = UniqueAppId();
        var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        // secondary with no IPC callbacks ─ should silently return false
        var secondary = CreateLock<string>(appId);
        secondary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Named Pipe Server Cardinality
    // "maxNumberOfServerInstances: 1 ─ prevents multiple listeners."
    //
    // We verify this indirectly: if the primary is listening, a second
    // InstanceLock that also acquired the lock (impossible with real OS
    // primitives, but tested via sequential acquire/release) would fail to
    // create a second pipe server. This is implicitly validated by the
    // concurrent race test ─ only one primary ever exists.
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void PipeServer_OnlyStartedWhenIpcCallbacksProvided()
    {
        var appId = UniqueAppId();

        // no IPC callbacks → no server task
        var primary = CreateLock<string>(appId);
        primary.TryAcquire(TestContext.Current.CancellationToken).ShouldBeTrue();

        var serverTask = primary._pipeServerLoopTask;

        serverTask.ShouldBeNull();
    }

    [Fact]
    public void PipeServer_StartedWhenIpcCallbacksProvided()
    {
        var appId = UniqueAppId();

        var primary = CreateLock(
            appId,
            createMsg: () => "x",
            onOtherInstance: _ => ValueTask.CompletedTask
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var serverTask = primary._pipeServerLoopTask;

        serverTask.ShouldNotBeNull();
        serverTask.IsCompleted.ShouldBeFalse();
    }

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Atomic Teardown ─ Dispose cancels the pipe CTS
    // "Invoking Dispose() must cancel the underlying _pipeCts."
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dispose_WhileServerWaiting_ServerLoopCompletesGracefully()
    {
        var appId = UniqueAppId();
        var primary = CreateLock(
            appId,
            createMsg: () => "x",
            onOtherInstance: _ => ValueTask.CompletedTask
        );

        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        // Server is now blocked on WaitForConnectionAsync. Dispose should cancel it.
        primary.Dispose();

        // The server task should complete without faulting.
        await AwaitServerLoopAsync(primary, TimeSpan.FromSeconds(5));

        var serverTask = primary._pipeServerLoopTask;
        serverTask?.Status.ShouldBe(TaskStatus.RanToCompletion);
    }
}
