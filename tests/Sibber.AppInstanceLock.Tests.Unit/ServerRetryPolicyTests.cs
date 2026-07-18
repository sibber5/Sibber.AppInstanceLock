// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.IO.Pipes;

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class ServerRetryPolicyTests : UnitTestBase
{
    private sealed class MockBackendLock(InstanceLockOptions options) : InstanceLockImpl<string>("mock_pipe", options, null)
    {
        public int ThrowCount;

        public override bool TryAcquirePrimary() => true;
        protected override void DisposeCore() { }

        protected override NamedPipeServerStream CreatePipeServer()
        {
            ThrowCount++;
            throw new IOException("Mock server failure");
        }
    }

    [InlineData(0, 50, 200, 1, 0)] // 0 retries => 1 throw, 0ms delay
    [InlineData(1, 50, 200, 2, 0)] // 1 retry => 2 throws, 0ms delay (attempt 0 -> delay 0)
    [InlineData(2, 50, 200, 3, 50)] // 2 retries => 3 throws, 50ms delay (0 + 50)
    [InlineData(3, 50, 200, 4, 150)] // 3 retries => 4 throws, 0 + 50 + 100 = 150ms delay
    [InlineData(5, 10, 20, 6, 70)] // 5 retries => 6 throws, 0 + 10 + 20 + 20 + 20 = 70ms delay
    [Theory]
    public async Task InstanceServerRetryPolicy_BackoffAndRetries_WorkCorrectly(int maxRetries, int baseDelayMs, int maxDelayMs, int expectedThrows, int expectedDelayMs)
    {
        var options = new InstanceLockOptions
        {
            InstanceServerRetryPolicy = new InstanceServerRetryPolicy(
                MinimumUptime: TimeSpan.FromSeconds(10),
                BaseDelay: TimeSpan.FromMilliseconds(baseDelayMs),
                MaxDelay: TimeSpan.FromMilliseconds(maxDelayMs),
                MaxRetries: maxRetries
            ),
        };

        using var backend = new MockBackendLock(options);
        backend._isPrimary = true;

        var sw = Stopwatch.StartNew();

        // This will block until the server loop exhausts retries and terminates
        await backend.RunServerLoop(
            onMessage: _ => ValueTask.CompletedTask,
            onException: null,
            ct: TestContext.Current.CancellationToken
        );

        sw.Stop();

        backend.ThrowCount.ShouldBe(expectedThrows);

        // Account for thread scheduling overhead (e.g., allow ~20ms minimum for 0 delay if 0 retries isn't instant, though it should be fast).
        // Since xUnit can be slightly unpredictable, we use a small tolerance buffer for the lower bound.
        sw.ElapsedMilliseconds.ShouldBeGreaterThanOrEqualTo(Math.Max(0, expectedDelayMs - 30));
        sw.ElapsedMilliseconds.ShouldBeLessThan(expectedDelayMs + 2000);
    }
}
