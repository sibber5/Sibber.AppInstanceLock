// Copyright (c) 2025-2026 sibber (GitHub: sibber5)
//
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Sibber.AppInstanceLock;

internal abstract class InstanceLockImpl<TMessage>(string pipeName, InstanceLockOptions options, ILogger? logger) : IDisposable
{
    protected readonly string _pipeName = pipeName;
    protected readonly InstanceLockOptions _options = options;
    protected readonly ILogger? _logger = logger;

#pragma warning disable CA2213 // Disposable fields should be disposed - it is disposed.
    private CancellationTokenSource? _pipeCts;
#pragma warning restore CA2213
    private readonly Lock _pipeCtsLock = new();

    internal bool _disposed;

    internal bool? _isPrimary;

    protected static readonly bool IsMessageByte = typeof(TMessage) == typeof(byte) || typeof(TMessage) == typeof(sbyte) || typeof(TMessage) == typeof(bool) || (typeof(TMessage).IsEnum && typeof(TMessage).GetEnumUnderlyingType() == typeof(byte));

    // ReSharper disable once StaticMemberInGenericType - fine since there will likely only be a single instantiation of it for the whole program.
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = false,
        AllowTrailingCommas = false,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Creates a new, fresh NamedPipeServerStream (not connected).
    /// </summary>
    /// <exception cref="IOException"></exception>
    protected abstract NamedPipeServerStream CreatePipeServer();

    /// <remarks>This method is not thread-safe.</remarks>
    /// <returns><see langword="true"/> if the current instance is the primary instance, otherwise <see langword="false"/>.</returns>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    public abstract bool TryAcquirePrimary(); // no point in making this async because we don't want the app to start before checking if it's the primary instance anyway.

    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    /// <exception cref="IOException"></exception>
    // ExceptionAdjustment: M:System.Threading.Interlocked.Exchange``1(``0@,``0) -T:System.NotSupportedException
    public Task RunServerLoop(Func<TMessage, ValueTask> onMessage, Func<Exception, bool>? onException, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_isPrimary != true) throw new UnreachableException();
        if (Volatile.Read(ref _pipeCts) is not null) throw new UnreachableException($"{nameof(_pipeCts)} is not null. Cannot run server loop while notifying primary instance.");
        ArgumentNullException.ThrowIfNull(onMessage);

#pragma warning disable Ex0100
        return Task.Run(async () =>
#pragma warning restore Ex0100
        {
            lock (_pipeCtsLock)
            {
                try
                {
                    if (_pipeCts is not null || ct.IsCancellationRequested) return;
                    _pipeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    ct = _pipeCts.Token;
                }
                catch (Exception ex)
                {
                    _pipeCts?.Dispose();
                    _pipeCts = null;
                    _logger?.LogError(ex, "Exception thrown in " + nameof(RunServerLoop) + " task while linking cancellation token, before starting server loop. Exiting server loop task...");
                    return;
                }
            }

            var attempt = 0;
            var delay = TimeSpan.Zero;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var startTime = Stopwatch.GetTimestamp();
                    try
                    {
                        await RunLoop(ct).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogWarning(ex, "Primary instance pipe server loop threw an exception.");

                        Debug.Assert(attempt >= 0);
                        var retryPolicy = _options.InstanceServerRetryPolicy;
                        var uptime = Stopwatch.GetElapsedTime(startTime);
                        if (uptime >= retryPolicy.MinimumUptime) attempt = 0; // reset backoff if uptime exceeds minimum.
                        delay = attempt switch
                        {
                            0 => TimeSpan.Zero,
                            1 => retryPolicy.InitialDelay,
                            _ => delay * 2,
                        };
                        Debug.Assert(delay >= TimeSpan.Zero);

                        var confirmRetry = onException?.Invoke(ex) ?? true;
                        var retry = confirmRetry
                                && retryPolicy.MinimumUptime != Timeout.InfiniteTimeSpan
                                && (attempt > 0 || uptime >= retryPolicy.MinimumUptime)
                                && delay <= retryPolicy.MaxDelay;

                        const string RetryAttrTemplate = " " + nameof(onException) + "={ConfirmRetry}, Uptime={Uptime}, RetryAttempt={RetryAttempt}, Delay={Delay}ms, RetryPolicy={RetryPolicy}";
                        if (!retry)
                        {
                            _logger?.LogInformation(ex, "Terminating primary instance pipe server loop..." + RetryAttrTemplate, confirmRetry, uptime, attempt, delay.TotalMilliseconds, retryPolicy);
                            break;
                        }
                        _logger?.LogInformation(ex, "Restarting primary instance pipe server loop..." + RetryAttrTemplate, confirmRetry, uptime, attempt, delay.TotalMilliseconds, retryPolicy);

                        attempt++;
                        if (delay == TimeSpan.Zero) continue;

                        try
                        {
                            await Task.Delay(delay, ct).ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) { break; }
                    }
                }
            }
            finally
            {
                var cts = Interlocked.Exchange(ref _pipeCts, null);
                cts?.Dispose();

                _logger?.LogInformation("Primary instance pipe server loop terminated.");
            }
        }, CancellationToken.None); // don't pass ct because we don't want the returned task to throw, and it's very unlikely for the ct to get canceled before the task runs.

        async Task RunLoop(CancellationToken loopCt)
        {
            while (!loopCt.IsCancellationRequested)
            {
                try
                {
                    // ReSharper disable once UseAwaitUsing
                    using var pipe = CreatePipeServer(); // recreate server for next connection
                    await pipe.WaitForConnectionAsync(loopCt).ConfigureAwait(false);

                    bool read;
                    TMessage message = default!;

                    if (IsMessageByte)
                    {
                        Debug.Assert(Unsafe.SizeOf<TMessage>() == 1 && typeof(TMessage).IsValueType);
                        var msgRaw = pipe.ReadByte();
                        read = msgRaw != -1;
                        if (read) message = Unsafe.BitCast<byte, TMessage>((byte)msgRaw);
                    }
                    else
                    {
                        (read, message) = await TryReadMessage(pipe, loopCt).ConfigureAwait(false);
                    }

                    if (loopCt.IsCancellationRequested) break;
                    if (!read) continue;

                    try
                    {
                        await onMessage(message).ConfigureAwait(false);
                    }
                    catch (Exception onMessageEx)
                    {
                        _logger?.LogError(onMessageEx, $"Primary instance pipe server {nameof(onMessage)} threw an exception.");
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
            }
        }

        static async ValueTask<(bool, TMessage)> TryReadMessage(NamedPipeServerStream pipe, CancellationToken ct)
        {
            var buf = new byte[256];

            var lenBuf = buf.AsMemory(0, 4);
            try
            {
                await pipe.ReadExactlyAsync(lenBuf, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException)
            {
                return (false, default!); // when returning false the message will be ignored, so suppressing the nullable warning is fine.
            }

            var length = BinaryPrimitives.ReadInt32LittleEndian(lenBuf.Span);
            const int OneMiB = 1024 * 1024;
            if (length > OneMiB) throw new InvalidOperationException($"Message is too large. Maximum length is 1MiB. Message length is {(double)length / OneMiB}MiB.");
            if (length <= 0) throw new UnreachableException($"Message is less than or equal to 0 bytes. ({length} bytes)");

            var msgBuf = length <= buf.Length ? buf.AsMemory(0, length) : new byte[length];
            try
            {
                await pipe.ReadExactlyAsync(msgBuf, ct).ConfigureAwait(false);
            }
            catch (EndOfStreamException ex)
            {
                throw new IOException($"{nameof(EndOfStreamException)} was thrown before fully reading message. Pipe possibly unexpectedly disconnected.", ex);
            }

            var msg = JsonSerializer.Deserialize<TMessage>(msgBuf.Span, _jsonOptions);
            return (true, msg!); // the message that is deserialized was serialized from TMessage, so if TMessage is a non-nullable reference type, msg is guaranteed to be non-null.
        }
    }

    /// <exception cref="ArgumentException">The message created by <paramref name="createNotificationMessage"/> after serialization is too large (greater than 1MB).</exception>
    /// <exception cref="ObjectDisposedException"></exception>
    // ExceptionAdjustment: M:System.Threading.Interlocked.Exchange``1(``0@,``0) -T:System.NotSupportedException
    // ExceptionAdjustment: P:System.Array.Length -T:System.OverflowException
    public void NotifyExistingInstance(Func<TMessage> createNotificationMessage, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_isPrimary != false) throw new UnreachableException();
        if (Volatile.Read(ref _pipeCts) is not null) throw new UnreachableException($"{nameof(_pipeCts)} is not null. Cannot notify primary instance while server loop is running.");

        _logger?.LogDebug(nameof(NotifyExistingInstance) + ": notify primary via pipe={Pipe}", _pipeName);

        lock (_pipeCtsLock)
        {
            if (_pipeCts is not null || ct.IsCancellationRequested) return;
            _pipeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = _pipeCts.Token;
        }

        // the best way I could find to do a cancellable synchronous wait (for the delay between retries).
        // we create the object only if the first attempt fails, because in most cases it probably won't.
        ManualResetEventSlim? infWaitEvent = null;
        try
        {
#pragma warning disable Ex0100
            Debug.Assert(!IsMessageByte || (Unsafe.SizeOf<TMessage>() == 1 && typeof(TMessage).IsValueType));
            var message = IsMessageByte
                ? [Unsafe.BitCast<TMessage, byte>(createNotificationMessage())]
                : JsonSerializer.SerializeToUtf8Bytes(createNotificationMessage(), _jsonOptions);
#pragma warning restore Ex0100

            const int OneMiB = 1024 * 1024;
            if (message.Length > OneMiB) throw new ArgumentException($"Notification message is too large. Maximum length is 1MiB. Message length is {(double)message.Length / OneMiB}MiB.", nameof(createNotificationMessage));

            var totalAttempts = _options.NotifyInstanceRetryPolicy.Attempts + 1;
            for (var attempt = 0; attempt < totalAttempts && !ct.IsCancellationRequested; attempt++) // TODO: maybe dont break (remove the !IsCancellationRequested from the for loop) so that we dont log "failed to notify".
            {
                var success = NotifyPipe(message, _options.NotifyInstanceRetryPolicy.ConnectionTimeout);
                if (success) return;

                try
                {
                    var delay = Random.Shared.NextDouble() * _options.NotifyInstanceRetryPolicy.Delay;
                    infWaitEvent ??= new(false);
                    // synchronously block here to halt the secondary instance from continuing its startup sequence while we attempt to notify the primary instance.
                    infWaitEvent.Wait(delay, ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    _logger?.LogWarning(nameof(NotifyExistingInstance) + ": attempted to wait before a retry but '" + nameof(infWaitEvent) + "' was disposed, unexpectedly. assuming the CT was cancelled...");
                    infWaitEvent = null;
                    break;
                }
            }

            _logger?.LogWarning(nameof(NotifyExistingInstance) + ": failed to notify after {Attempts} attempts.", totalAttempts);
        }
        finally
        {
            var cts = Interlocked.Exchange(ref _pipeCts, null);
            cts?.Dispose();
            infWaitEvent?.Dispose();
        }
    }

    private bool NotifyPipe(ReadOnlySpan<byte> message, TimeSpan connectTimeout)
    {
        Debug.Assert(!_disposed && _isPrimary is false);

        using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out, PipeOptions.None);

        try
        {
            client.Connect(connectTimeout);

            if (IsMessageByte)
            {
                Debug.Assert(message.Length is 1);
                client.WriteByte(message[0]);
            }
            else
            {
                Span<byte> lenBuf = stackalloc byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(lenBuf, message.Length);
                client.Write(lenBuf);
                client.Write(message);
            }

#pragma warning disable CA1849
            client.Flush();
#pragma warning restore CA1849
            return true;
        }
        catch (TimeoutException)
        {
            _logger?.LogDebug(nameof(NotifyPipe) + ": connect/write timed out for pipe {Pipe}.", _pipeName);
            return false;
        }
        catch (OperationCanceledException)
        {
            _logger?.LogDebug(nameof(NotifyPipe) + ": connect/write canceled for pipe {Pipe}.", _pipeName);
            return false;
        }
        catch (IOException ioEx)
        {
            // Typical when pipe not present or race; tolerate and return false for retry logic.
            _logger?.LogDebug(ioEx, nameof(NotifyPipe) + ": IO error for pipe {Pipe}.", _pipeName);
            return false;
        }
        catch (Exception ex)
        {
            // Unexpected; log and return false so the caller may retry or fail gracefully.
            _logger?.LogWarning(ex, nameof(NotifyPipe) + ": unexpected error for pipe {Pipe}.", _pipeName);
            return false;
        }
    }

    // ExceptionAdjustment: M:System.Threading.Interlocked.Exchange``1(``0@,``0) -T:System.NotSupportedException
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;

        var cts = Interlocked.Exchange(ref _pipeCts, null);
        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        DisposeCore();
    }

    protected abstract void DisposeCore();
}
