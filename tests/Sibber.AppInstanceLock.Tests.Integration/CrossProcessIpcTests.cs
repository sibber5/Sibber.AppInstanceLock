// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class CrossProcessIpcTests : IntegrationTestBase
{
    [Fact]
    public async Task CrossProcessIpc_MessageReceivedSuccessfully()
    {
        var appId = UniqueAppId();

        using var p1 = StartHarness(appId, "--listen");
        try
        {
            var line = await p1.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            line.ShouldBe("ACQUIRED");

            using var p2 = StartHarness(appId, "--message \"Hello_From_P2\"");
            try
            {
                var line2 = await p2.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                line2.ShouldBe("NOT_PRIMARY");

                var msgLine = await p1.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                msgLine.ShouldBe("RECEIVED_MESSAGE:Hello_From_P2");
            }
            finally
            {
                if (!p2.HasExited) p2.Kill();
            }
        }
        finally
        {
            if (!p1.HasExited) p1.Kill();
        }
    }

    [Fact]
    public async Task ColdStartResilience_ConcurrentStartup()
    {
        var appId = UniqueAppId();

        // Spawn both concurrently to create a race condition.
        // One will become Primary and start its pipe server (which takes a few milliseconds due to Task.Run).
        // The other will become Secondary, fail to connect initially, and rely on the retry policy to succeed.
        // By making both processes symmetric, it doesn't matter which one wins the OS-level race.
        using var p1 = StartHarness(appId, "--listen --message \"ColdStart\" --retry-attempts 20 --retry-delay-ms 500");
        using var p2 = StartHarness(appId, "--listen --message \"ColdStart\" --retry-attempts 20 --retry-delay-ms 500");

        try
        {
            var p1Task = p1.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
            var p2Task = p2.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();

            var results = await Task.WhenAll(p1Task, p2Task).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // One must be ACQUIRED, the other NOT_PRIMARY
            results.ShouldContain("ACQUIRED");
            results.ShouldContain("NOT_PRIMARY");

            // The one that is listening (which is the primary) should receive the message
            var msg1Task = p1.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();
            var msg2Task = p2.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask();

            var msgs = await Task.WhenAll(msg1Task, msg2Task).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            msgs.ShouldContain("RECEIVED_MESSAGE:ColdStart");
            msgs.ShouldContain((string?)null);
        }
        finally
        {
            if (!p1.HasExited) p1.Kill();
            if (!p2.HasExited) p2.Kill();
        }
    }
}
