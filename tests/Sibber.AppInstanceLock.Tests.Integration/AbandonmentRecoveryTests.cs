// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Integration;

public sealed class AbandonmentRecoveryTests : IntegrationTestBase
{
    [Fact]
    public async Task CrashWithoutDispose_ReleasesLock_AllowingNewAcquisition()
    {
        var appId = UniqueAppId();
        var p1 = StartHarness(appId);

        try
        {
            // Wait for ACQUIRED
            var line = await p1.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            line.ShouldBe("ACQUIRED");

            // Forcefully terminate Process 1 (Crash / SIGKILL)
            p1.Kill(entireProcessTree: true);
            await p1.WaitForExitAsync(TestContext.Current.CancellationToken);

            // Start Process 2
            using var p2 = StartHarness(appId);
            try
            {
                var line2 = await p2.StandardOutput.ReadLineAsync(TestContext.Current.CancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
                line2.ShouldBe("ACQUIRED");
            }
            finally
            {
                if (!p2.HasExited)
                {
                    p2.Kill();
                }
            }
        }
        finally
        {
            if (!p1.HasExited)
            {
                p1.Kill();
            }
        }
    }
}
