// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Sibber.AppInstanceLock.Tests.Unit;

public sealed class ConstructorValidationTests : UnitTestBase
{

    // ──────────────────────────────────────────────────────────────────────
    // INVARIANT: Constructor argument validation
    // ──────────────────────────────────────────────────────────────────────

    [Fact]
    public void Constructor_NullAppId_Throws() => Assert.Throws<ArgumentNullException>(() => new InstanceLock<string>(null!));

    [Fact]
    public void Constructor_EmptyAppId_DoesNotThrowInConstructor()
    {
        // The production code validates characters via foreach, which vacuously passes
        // for empty strings. This test documents that behavior ─ empty strings are not
        // rejected at construction time.
        var ex = Record.Exception(() => new InstanceLock<string>(""));
        Assert.Null(ex);
    }

    [Fact]
    public void Constructor_InvalidCharsInAppId_Throws()
    {
        Assert.Throws<ArgumentException>(() => new InstanceLock<string>("my.app"));
        Assert.Throws<ArgumentException>(() => new InstanceLock<string>("my app"));
        Assert.Throws<ArgumentException>(() => new InstanceLock<string>("app/id"));
    }

    [Fact]
    public void Constructor_AppIdTooLong_Throws()
    {
        var tooLong = new string('a', 256);
        Assert.Throws<ArgumentOutOfRangeException>(() => new InstanceLock<string>(tooLong));
    }

    [Fact]
    public void Constructor_OnlyOneMsgCallbackProvided_Throws()
    {
        // createMsg without onOtherInstance
        Assert.Throws<ArgumentNullException>(() =>
            new InstanceLock<string>("valid-id", createMsgToPrimary: () => "x"));

        // onOtherInstance without createMsg
        Assert.Throws<ArgumentNullException>(() =>
            new InstanceLock<string>("valid-id", onOtherInstanceOpened: _ => ValueTask.CompletedTask));
    }
}
