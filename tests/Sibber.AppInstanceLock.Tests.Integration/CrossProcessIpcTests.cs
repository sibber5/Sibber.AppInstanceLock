// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class CrossProcessIpcTests : IntegrationTestBase
{
    private static string GetHarnessDll()
    {
        var integrationDll = typeof(CrossProcessIpcTests).Assembly.Location;
        var harnessDll = integrationDll.Replace("Sibber.AppInstanceLock.Tests.Integration", "Sibber.AppInstanceLock.TestHarness", StringComparison.Ordinal);
        if (!File.Exists(harnessDll))
            throw new FileNotFoundException($"Could not find harness dll at {harnessDll}");
        return harnessDll;
    }

    private static Process StartHarness(string appId, string extraArgs = "")
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{GetHarnessDll()}\" --parent-pid {Environment.ProcessId} --app-id {appId} {extraArgs}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        var p = Process.Start(psi);
        Assert.NotNull(p);
        return p;
    }

    [Fact]
    [SuppressMessage("Usage", "xUnit1051:Calls to methods which accept CancellationToken should use TestContext.Current.CancellationToken")]
    public async Task CrossProcessIpc_MessageReceivedSuccessfully()
    {
        var appId = UniqueAppId();

        using var p1 = StartHarness(appId, "--listen");
        try
        {
            var line = await p1.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal("ACQUIRED", line);

            using var p2 = StartHarness(appId, "--message \"Hello_From_P2\"");
            try
            {
                var line2 = await p2.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.Equal("NOT_PRIMARY", line2);

                var msgLine = await p1.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                Assert.Equal("RECEIVED_MESSAGE:Hello_From_P2", msgLine);
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
    [SuppressMessage("Usage", "xUnit1051:Calls to methods which accept CancellationToken should use TestContext.Current.CancellationToken")]
    public async Task ColdStartResilience_ConcurrentStartup()
    {
        var appId = UniqueAppId();

        // Spawn both concurrently to create a race condition.
        // One will become Primary and start its pipe server (which takes a few milliseconds due to Task.Run).
        // The other will become Secondary, fail to connect initially, and rely on the retry policy to succeed.
        using var p1 = StartHarness(appId, "--listen");
        using var p2 = StartHarness(appId, "--message \"ColdStart\" --retry-attempts 20 --retry-delay-ms 500");

        try
        {
            var p1Task = p1.StandardOutput.ReadLineAsync();
            var p2Task = p2.StandardOutput.ReadLineAsync();

            var results = await Task.WhenAll(p1Task, p2Task).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

            // One must be ACQUIRED, the other NOT_PRIMARY
            Assert.Contains("ACQUIRED", results);
            Assert.Contains("NOT_PRIMARY", results);

            // The one that is listening (p1) should receive the message
            var msgLine = await p1.StandardOutput.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            Assert.Equal("RECEIVED_MESSAGE:ColdStart", msgLine);
        }
        finally
        {
            if (!p1.HasExited) p1.Kill();
            if (!p2.HasExited) p2.Kill();
        }
    }
}
