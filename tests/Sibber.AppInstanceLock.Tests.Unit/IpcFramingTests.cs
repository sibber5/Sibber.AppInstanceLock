// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class IpcFramingTests : UnitTestBase
{
    [Fact]
    public void MaxPayloadSize_Succeeds()
    {
        var appId = UniqueAppId();
        using var primary = CreateLock<string>(appId, createMsg: () => "", onOtherInstance: _ => ValueTask.CompletedTask);
        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        // The JSON serialization wraps the string in quotes ("...").
        // 1 MiB = 1,048,576 bytes.
        // We need the string length to be exactly 1,048,576 - 2 = 1,048,574.
        var maxPayload = new string('A', 1_048_574);
        using var secondary = CreateLock<string>(appId, createMsg: () => maxPayload, onOtherInstance: _ => ValueTask.CompletedTask);

        Assert.False(secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken));
    }

    [Fact]
    public void PayloadExceedsMaxSize_ThrowsArgumentException()
    {
        var appId = UniqueAppId();
        using var primary = CreateLock<string>(appId, createMsg: () => "", onOtherInstance: _ => ValueTask.CompletedTask);
        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        var tooLargePayload = new string('A', 1048575); // 1048575 + 2 = 1048577 bytes
        using var secondary = CreateLock<string>(appId, createMsg: () => tooLargePayload, onOtherInstance: _ => ValueTask.CompletedTask);

        // ReSharper disable once AccessToDisposedClosure
        var ex = Record.Exception(() => secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken));
        Assert.IsType<ArgumentException>(ex);
    }

    [Fact]
    public async Task ZeroBytePayload_Succeeds()
    {
        var appId = UniqueAppId();
        var received = false;
        using var primary = CreateLock<string>(appId, createMsg: () => "", onOtherInstance: b =>
        {
            Assert.Equal("", b);
            received = true;
            return ValueTask.CompletedTask;
        });
        Assert.True(primary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        var zeroPayload = "";
        using var secondary = CreateLock<string>(appId, createMsg: () => zeroPayload, onOtherInstance: _ => ValueTask.CompletedTask);

        Assert.False(secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken));

        // Wait for primary to process
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(received);
    }
}
