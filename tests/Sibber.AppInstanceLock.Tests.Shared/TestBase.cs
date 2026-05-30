// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.Versioning;

#pragma warning disable CA1716 // Identifiers should not match keywords ('Shared' is only a visual basic keyword so who cares)
namespace Sibber.AppInstanceLock.Tests.Shared;
#pragma warning restore CA1716

#pragma warning disable CA1063 // Implement IDisposable Correctly
#pragma warning disable CA1816 // Dispose methods should call SuppressFinalize
#pragma warning disable CA1515 // Consider making public types internal
#pragma warning disable CA1051 // Do not declare visible instance fields
#pragma warning disable CA1002 // Do not expose generic lists

[SupportedOSPlatform("windows")]
[SupportedOSPlatform("linux")]
[SupportedOSPlatform("macos")]
public abstract class TestBase : IDisposable
{
    protected readonly List<IDisposable> _disposables = [];

    protected abstract string Prefix { get; }

    protected string UniqueAppId() => $"test-{Prefix}-{Guid.NewGuid():N}";

    protected InstanceLock<TMessage> CreateLock<TMessage>(
        string appId,
        Func<TMessage>? createMsg = null,
        Func<TMessage, ValueTask>? onOtherInstance = null,
        Func<Exception, bool>? onServerException = null,
        InstanceLockOptions? options = null
    )
    {
        var l = new InstanceLock<TMessage>(appId, createMsg, onOtherInstance, onServerException, options: options);
        _disposables.Add(l);
        return l;
    }

    public virtual void Dispose()
    {
        foreach (var d in _disposables)
        {
            try { d.Dispose(); }
            catch { /* best-effort cleanup */ }
        }
    }
}
