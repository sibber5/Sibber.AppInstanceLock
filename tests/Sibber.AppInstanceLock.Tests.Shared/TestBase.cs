// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Meziantou.Extensions.Logging.Xunit.v3;

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
public abstract class TestBase : IDisposable, IAsyncDisposable
{
    protected readonly List<IDisposable> _disposables = [];
    private readonly List<Func<Task?>> _serverLoopGetters = [];

    protected abstract string Prefix { get; }

    protected string UniqueAppId() => $"test-{Prefix}-{Guid.NewGuid():N}";

    protected TestBase()
    {
        if (OperatingSystem.IsLinux())
        {
            Environment.SetEnvironmentVariable("XDG_SESSION_ID", "ci_mock_session");
        }
    }

    protected InstanceLock<TMessage> CreateLock<TMessage>(
        string appId,
        Func<TMessage>? createMsg = null,
        Func<TMessage, ValueTask>? onOtherInstance = null,
        Func<Exception, bool>? onServerException = null,
        InstanceLockOptions? options = null
    )
    {
        var loggerFactory = LoggerFactory.Create(b =>
        {
            b.AddXunit();
            b.SetMinimumLevel(LogLevel.Debug);
        });
        var l = new InstanceLock<TMessage>(appId, createMsg, onOtherInstance, onServerException, options: options, loggerFactory: loggerFactory);
        _disposables.Add(l);
        _disposables.Add(loggerFactory);
        _serverLoopGetters.Add(() => l._pipeServerLoopTask);
        return l;
    }

    public virtual void Dispose()
    {
        foreach (var d in _disposables)
        {
            d.Dispose();
        }
    }

    public virtual async ValueTask DisposeAsync()
    {
        foreach (var d in _disposables)
        {
            if (d is IAsyncDisposable ad) await ad.DisposeAsync();
            else d.Dispose();
        }

        foreach (var getTask in _serverLoopGetters)
        {
            if (getTask() is { } t)
            {
                try
                {
                    await t.WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (OperationCanceledException) { }
            }
        }
    }
}
