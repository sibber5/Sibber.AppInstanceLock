// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Collections.Concurrent;
using System.Diagnostics;

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class ConcurrentIpcStressTests : IntegrationTestBase
{
    [Fact]
    public async Task InProcess_ConcurrentSecondaries_AllMessagesReceived()
    {
        var appId = UniqueAppId();
        const int InstanceCount = 30;

        var receivedMessages = new ConcurrentBag<string>();
        var allReceivedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var primaryOptions = new InstanceLockOptions
        {
            Scope = InstanceLockScope.User
        };

        var primary = CreateLock(
            appId,
            createMsg: () => "primary",
            onOtherInstance: msg =>
            {
                receivedMessages.Add(msg);
                if (receivedMessages.Count == InstanceCount)
                {
                    allReceivedTcs.TrySetResult();
                }
                return ValueTask.CompletedTask;
            },
            options: primaryOptions
        );

        // 1. Primary acquires lock
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        // 2. Prepare secondary options with generous retries and jitter to handle concurrency
        var secondaryOptions = new InstanceLockOptions
        {
            Scope = InstanceLockScope.User,
            NotificationRetryPolicy = new NotificationRetryPolicy(
                RetryAttempts: 150,
                MaxJitterDelay: TimeSpan.FromMilliseconds(500),
                ConnectionTimeout: TimeSpan.FromSeconds(2)
            )
        };

        var startSignal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = new Task[InstanceCount];

        for (var i = 0; i < InstanceCount; i++)
        {
            var index = i;
            tasks[i] = Task.Run(async () =>
            {
                var secondary = CreateLock(
                    appId,
                    createMsg: () => $"msg-{index}",
                    onOtherInstance: _ => ValueTask.CompletedTask,
                    options: secondaryOptions
                );

                // Wait asynchronously for the start signal so we do not block thread pool threads
                await startSignal.Task;

                var result = secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken);
                result.ShouldBeFalse($"Instance {index} should not become primary.");
            }, TestContext.Current.CancellationToken);
        }

        // Trigger concurrent execution of all secondary locks
        startSignal.SetResult();

        // 3. Wait for all secondary tasks to complete their attempts
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

        // 4. Wait for all messages to be received by the primary
        await allReceivedTcs.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        // 5. Verify the results
        receivedMessages.Count.ShouldBe(InstanceCount);
        var expectedMessages = Enumerable.Range(0, InstanceCount).Select(i => $"msg-{i}").ToList();
        foreach (var expected in expectedMessages)
        {
            receivedMessages.ShouldContain(expected);
        }
    }

    [Fact]
    public async Task CrossProcess_ConcurrentSecondaries_AllMessagesReceived()
    {
        var appId = UniqueAppId();
        const int ProcessCount = 10;

        // 1. Start primary harness in listen mode
        using var primaryProcess = StartHarness(appId, "--listen --run-forever");
        try
        {
            var primaryOutput = primaryProcess.StandardOutput;
            var line = await primaryOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            line.ShouldBe("ACQUIRED");

            // 2. Launch secondary processes concurrently
            var secondaries = new List<Process>();
            try
            {
                for (var i = 0; i < ProcessCount; i++)
                {
                    // Use generous retry attempts/delay to ensure concurrency stress doesn't drop messages
                    var p = StartHarness(appId, $"--message \"cp-msg-{i}\" --retry-attempts 150 --retry-delay-ms 500");
                    secondaries.Add(p);
                }

                // 3. Wait for all secondary processes to exit and verify they were not primary
                var exitTasks = secondaries.Select(async (p, idx) =>
                {
                    var output = await p.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
                    output.ShouldBe("NOT_PRIMARY");
                    await p.WaitForExitAsync(TestContext.Current.CancellationToken)
                        .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
                }).ToList();

                await Task.WhenAll(exitTasks).WaitAsync(TimeSpan.FromSeconds(45), TestContext.Current.CancellationToken);

                // 4. Read the messages received by the primary from its stdout
                var receivedMessages = new List<string>();
                for (var i = 0; i < ProcessCount; i++)
                {
                    var msgLine = await primaryOutput.ReadLineAsync(TestContext.Current.CancellationToken)
                        .AsTask()
                        .WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
                    msgLine.ShouldStartWith("RECEIVED_MESSAGE:");
                    receivedMessages.Add(msgLine);
                }

                // 5. Verify all messages are received
                receivedMessages.Count.ShouldBe(ProcessCount);
                for (var i = 0; i < ProcessCount; i++)
                {
                    receivedMessages.ShouldContain($"RECEIVED_MESSAGE:cp-msg-{i}");
                }
            }
            finally
            {
                foreach (var p in secondaries)
                {
                    if (!p.HasExited)
                    {
                        try { p.Kill(); } catch { }
                    }
                }
            }
        }
        finally
        {
            if (!primaryProcess.HasExited)
            {
                try { primaryProcess.Kill(); } catch { }
            }
        }
    }
}
