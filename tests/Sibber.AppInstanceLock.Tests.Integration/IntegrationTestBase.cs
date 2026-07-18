// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

global using Shouldly;
using System.Diagnostics;
using Sibber.AppInstanceLock.Tests.Shared;

namespace Sibber.AppInstanceLock.Tests.Integration;

public abstract class IntegrationTestBase : TestBase
{
    protected override string Prefix => "integ";

    private static string GetHarnessDll()
    {
        var integrationDll = typeof(IntegrationTestBase).Assembly.Location;
        var harnessDll = integrationDll.Replace($"{nameof(Sibber)}.{nameof(AppInstanceLock)}.{nameof(Tests)}.{nameof(Integration)}", $"{nameof(Sibber)}.{nameof(AppInstanceLock)}.TestHarness", StringComparison.Ordinal);
        if (!File.Exists(harnessDll)) throw new FileNotFoundException($"Could not find harness dll at {harnessDll}");
        return harnessDll;
    }

    private protected static Process StartHarness(string appId, string extraArgs = "")
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = $"\"{GetHarnessDll()}\" --parent-pid {Environment.ProcessId} --app-id {appId} {extraArgs}",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
        };
        var p = Process.Start(psi);
        p.ShouldNotBeNull();
        return p;
    }
}
