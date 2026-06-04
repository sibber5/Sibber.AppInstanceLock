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
        using var primary = CreateLock(appId, createMsg: () => "", onOtherInstance: _ => ValueTask.CompletedTask);
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        // The JSON serialization wraps the string in quotes ("...").
        // 1 MiB = 1,048,576 bytes.
        // We need the string length to be exactly 1,048,576 - 2 = 1,048,574.
        var maxPayload = new string('A', 1_048_574);
        using var secondary = CreateLock(appId, createMsg: () => maxPayload, onOtherInstance: _ => ValueTask.CompletedTask);

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();
    }

    [Fact]
    public void PayloadExceedsMaxSize_ThrowsArgumentException()
    {
        var appId = UniqueAppId();
        using var primary = CreateLock(appId, createMsg: () => "", onOtherInstance: _ => ValueTask.CompletedTask);
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var tooLargePayload = new string('A', 1048575); // 1048575 + 2 = 1048577 bytes
        using var secondary = CreateLock(appId, createMsg: () => tooLargePayload, onOtherInstance: _ => ValueTask.CompletedTask);

        // ReSharper disable once AccessToDisposedClosure
        var ex = Record.Exception(() => secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken));
        ex.ShouldBeOfType<ArgumentException>();
    }

    [Fact]
    public async Task ZeroBytePayload_Succeeds()
    {
        var appId = UniqueAppId();
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var primary = CreateLock(appId, createMsg: () => "", onOtherInstance: b =>
        {
            b.ShouldBe("");
            tcs.TrySetResult();
            return ValueTask.CompletedTask;
        });
        primary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeTrue();

        var zeroPayload = "";
        using var secondary = CreateLock(appId, createMsg: () => zeroPayload, onOtherInstance: _ => ValueTask.CompletedTask);

        secondary.TryAcquireOrNotify(TestContext.Current.CancellationToken).ShouldBeFalse();

        // Wait for primary to process
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }
}
