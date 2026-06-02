// Copyright (c) 2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using System.Globalization;
using Sibber.AppInstanceLock;

#pragma warning disable CA1849
#pragma warning disable CA1303

var parentPid = 0;
var appId = "";
var message = "";
var hasMessage = false;
var listen = false;
var retryAttempts = 0;
var retryDelayMs = 0;
var waitBeforeStartMs = 0;
var runForever = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--parent-pid":
            parentPid = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--app-id":
            appId = args[++i];
            break;
        case "--message":
            message = args[++i];
            hasMessage = true;
            break;
        case "--listen":
            listen = true;
            break;
        case "--retry-attempts":
            retryAttempts =
                int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--retry-delay-ms":
            retryDelayMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--wait-before-start-ms":
            waitBeforeStartMs = int.Parse(args[++i], CultureInfo.InvariantCulture);
            break;
        case "--run-forever":
            runForever = true;
            break;
        default:
            throw new NotImplementedException($"Argument {args[i]} is not implemented.");
    }
}

var parent = Process.GetProcessById(parentPid);
new Thread(() =>
{
    try
    {
        parent.WaitForExit();
    }
    catch
    {
        /* Process not found or exited */
    }

    Environment.Exit(1); // Force terminate
})
{
    IsBackground = true,
}.Start();

if (waitBeforeStartMs > 0)
{
    Thread.Sleep(waitBeforeStartMs);
}

var options = new InstanceLockOptions
{
    Scope = InstanceLockScope.User,
    NotificationRetryPolicy = new NotificationRetryPolicy(
        RetryAttempts: retryAttempts,
        MaxJitterDelay: TimeSpan.FromMilliseconds(retryDelayMs),
        ConnectionTimeout: TimeSpan.FromSeconds(2)
    )
};

#pragma warning disable CA2000
var msgReceivedEvent = new ManualResetEventSlim(false);
#pragma warning restore CA2000

Func<string, ValueTask>? onOtherInstance =
    (listen || hasMessage)
    ? msg =>
    {
        if (listen)
        {
            Console.WriteLine($"RECEIVED_MESSAGE:{msg}");
            Console.Out.Flush();
            msgReceivedEvent.Set();
        }

        return ValueTask.CompletedTask;
    }
#pragma warning disable IDE0055
    : null;
#pragma warning restore IDE0055

Func<string>? createMsg = (listen || hasMessage) ? () => message : null;

using var instanceLock = new InstanceLock<string>(
    appId,
    createMsgToPrimary: createMsg,
    onOtherInstanceOpened: onOtherInstance,
    onServerException: null,
    loggerFactory: null,
    options: options);

if (instanceLock.TryAcquireOrNotify())
{
    Console.WriteLine("ACQUIRED");
    Console.Out.Flush();

    if (runForever || listen)
    {
        if (listen)
        {
            msgReceivedEvent.Wait();
            // Allow time for the secondary process to observe completion before we exit
            Thread.Sleep(200);
        }
        else
        {
            Thread.Sleep(Timeout.Infinite);
        }
    }
}
else
{
    Console.WriteLine("NOT_PRIMARY");
    Console.Out.Flush();
}
