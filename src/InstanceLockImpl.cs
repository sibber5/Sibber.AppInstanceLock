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
    internal readonly string _pipeName = pipeName;
    protected readonly InstanceLockOptions _options = options;
    protected readonly ILogger? _logger = logger;

#pragma warning disable CA2213 // Disposable fields should be disposed - it is disposed.
    internal CancellationTokenSource? _pipeCts;
#pragma warning restore CA2213
    internal readonly Lock _pipeCtsLock = new();

    internal bool _disposed;

    internal bool? _isPrimary;

#if INCLUDE_TEST_HOOKS
    internal Action? OnBeforePipeCtsLock { get; set; }
    internal Action? OnBeforeServerLoopCleanup { get; set; }
    internal Action? OnBeforeDisposeCtsLock { get; set; }
    internal Action? OnServerReady { get; set; }
#endif

    protected static readonly bool IsSingleByteMessage = typeof(TMessage) == typeof(byte) || typeof(TMessage) == typeof(sbyte) || typeof(TMessage) == typeof(bool) || (typeof(TMessage).IsEnum && typeof(TMessage).GetEnumUnderlyingType() == typeof(byte));

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
    /// <exception cref="NotSupportedException"><see cref="InstanceLockOptions.Scope"/> is not a supported scope.</exception>
    /// <exception cref="InvalidOperationException">Windows Only: The <see cref="System.Security.Principal.SecurityIdentifier"/> for the current user was <see langword="null"/>.</exception>
    /// <exception cref="System.Security.SecurityException">The caller does not have the required permissions to determine the session or user identity on the current platform.</exception>
    protected abstract NamedPipeServerStream CreatePipeServer();

    /// <summary>
    /// Attempts to acquire the primary instance lock using the platform-specific mechanism.
    /// </summary>
    /// <remarks>
    /// This method is intentionally synchronous because the application must not proceed before
    /// determining whether it is the primary instance.
    /// </remarks>
    /// <note type="threadunsafe"><see cref="TryAcquirePrimary"/> is not thread-safe.</note>
    /// <returns><see langword="true"/> if the current instance is the primary instance, otherwise <see langword="false"/>.</returns>
    /// <exception cref="IOException"></exception>
    /// <exception cref="ObjectDisposedException"></exception>
    /// <exception cref="UnauthorizedAccessException">Unix Only: Failed to create the lock file because another user has already created a restrictive file at the path.</exception>
    public abstract bool TryAcquirePrimary();

    /// <summary>
    /// Starts the IPC named-pipe server loop on a background thread. The loop listens for incoming
    /// messages from secondary instances and dispatches them to <paramref name="onMessage"/>.
    /// </summary>
    /// <remarks>
    /// The returned <see cref="Task"/> completes when the server loop terminates (due to cancellation,
    /// dispose, or exhausting retries). It does not fault; all exceptions are handled internally via
    /// <paramref name="onException"/> and the configured <see cref="InstanceServerRetryPolicy"/>.
    /// <br/>
    /// Exceptions thrown by <paramref name="onMessage"/> are caught and logged, but do not terminate
    /// the server loop and are not passed to <paramref name="onException"/>.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="onMessage"/> is <see langword="null"/>.</exception>
    /// <exception cref="ObjectDisposedException">This instance has been disposed.</exception>
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
#if INCLUDE_TEST_HOOKS
            OnBeforePipeCtsLock?.Invoke();
#endif
            lock (_pipeCtsLock)
            {
                try
                {
                    if (Volatile.Read(ref _disposed)) return;
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

                        if (uptime >= retryPolicy.MinimumUptime)
                        {
                            // the server was stable long enough. reset backoff and attempt counter.
                            attempt = 0;
                            delay = TimeSpan.Zero;
                        }

                        var confirmRetry = onException?.Invoke(ex) ?? true;
                        var retry = confirmRetry && (attempt < retryPolicy.MaxRetries || retryPolicy.MaxRetries == -1);

                        delay = attempt switch
                        {
                            0 => TimeSpan.Zero,
                            1 => retryPolicy.BaseDelay,
                            _ => delay * 2,
                        };
                        if (delay > retryPolicy.MaxDelay) delay = retryPolicy.MaxDelay;
                        Debug.Assert(delay >= TimeSpan.Zero);

                        const string RetryAttrTemplate = " " + nameof(onException) + "->{ConfirmRetry}, Uptime={Uptime}, RetryAttempt={RetryAttempt}, Delay={Delay}ms, RetryPolicy={RetryPolicy}";
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
#if INCLUDE_TEST_HOOKS
                OnBeforeServerLoopCleanup?.Invoke();
#endif
                var cts = Interlocked.Exchange(ref _pipeCts, null);
                cts?.Dispose();

                _logger?.LogInformation("Primary instance pipe server loop terminated.");
            }
        }, CancellationToken.None); // don't pass ct because we don't want the returned task to throw, and it's very unlikely for the ct to get canceled before the task runs.

        async Task RunLoop(CancellationToken loopCt)
        {
            // ReSharper disable once UseAwaitUsing
            using var pipe = CreatePipeServer(); // Let exceptions bubble up to outer retry policy.
#if INCLUDE_TEST_HOOKS
            OnServerReady?.Invoke();
#endif
            while (!loopCt.IsCancellationRequested)
            {
                Exception? finalEx = null;
                var brutallyDisposed = false;
                try
                {
                    await pipe.WaitForConnectionAsync(loopCt).ConfigureAwait(false);

                    bool read;
                    TMessage message = default!;

                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(loopCt);
                    timeoutCts.CancelAfter(TimeSpan.FromSeconds(3));

                    try
                    {
                        if (IsSingleByteMessage)
                        {
                            Debug.Assert(Unsafe.SizeOf<TMessage>() == 1 && typeof(TMessage).IsValueType);
                            brutallyDisposed = true;
                            await using var ctr = timeoutCts.Token.Register(static s => ((Stream)s!).Dispose(), pipe).ConfigureAwait(false);
                            var msgRaw = pipe.ReadByte();
                            read = msgRaw != -1;
                            if (read) message = Unsafe.BitCast<byte, TMessage>((byte)msgRaw);
                        }
                        else
                        {
                            (read, message) = await TryReadMessage(pipe, timeoutCts.Token).ConfigureAwait(false);
                        }
                    }
                    catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !loopCt.IsCancellationRequested)
                    {
                        // Timed out reading the message from the connected client
                        _logger?.LogInformation("Primary instance pipe server timed out waiting for message data.");
                        read = false;
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
                catch (IOException ioEx)
                {
                    _logger?.LogDebug(ioEx, "Primary instance pipe server threw IOException during connection/read. Client might have disconnected early.");
                }
                catch (Exception ex)
                {
                    finalEx = ex;
                }
                finally
                {
                    try
                    {
                        pipe.Disconnect();
                    }
                    catch (ObjectDisposedException) when (brutallyDisposed)
                    {
                        // Expected if the pipe was brutally disposed by the fast-path timeout.
                    }
                    catch (InvalidOperationException ex) when (ex is not ObjectDisposedException)
                    {
                        // Expected if no connection was made or client already disconnected.
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogDebug(ex, "Primary instance pipe server threw exception during Disconnect().");
                        // If we can't disconnect, the pipe is likely in a bad state, so throw to trigger outer retry logic which will recreate it.
                        if (finalEx is null) finalEx = ex;
                        else finalEx = new AggregateException(finalEx, ex);
                    }

#pragma warning disable CA2219 // Do not raise exceptions in finally clauses
                    if (finalEx is not null) throw finalEx;
#pragma warning restore CA2219
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
            if (length > OneMiB) throw new InvalidOperationException($"Message is too large. Maximum length is 1,048,576 bytes (1MiB). Message length is {length} bytes.");
            if (length < 0) throw new InvalidOperationException($"Message length is less than 0 bytes. ({length} bytes)");
            if (length == 0) return (true, default!);

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

    /// <exception cref="ArgumentException">The serialized message exceeds 1 MiB (1,048,576 bytes).</exception>
    /// <exception cref="ObjectDisposedException"></exception>
    // ExceptionAdjustment: P:System.Array.Length -T:System.OverflowException
    public void NotifyExistingInstance(Func<TMessage> createMsgToPrimary, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed), this);
        if (_isPrimary != false) throw new UnreachableException();
        if (Volatile.Read(ref _pipeCts) is not null) throw new UnreachableException($"{nameof(_pipeCts)} is not null. Cannot notify primary instance while server loop is running.");

        lock (_pipeCtsLock)
        {
            if (Volatile.Read(ref _disposed)) return;
            if (_pipeCts is not null || ct.IsCancellationRequested) return;
            _pipeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            ct = _pipeCts.Token;
        }

        _logger?.LogDebug(nameof(NotifyExistingInstance) + ": notify primary via pipe={Pipe}", _pipeName);

        // the best way I could find to do a cancellable synchronous wait (for the delay between retries).
        // we create the object only if the first attempt fails, because in most cases it probably won't.
        ManualResetEventSlim? infWaitEvent = null;
        try
        {
#pragma warning disable Ex0100 // NotSupportedException from BitCast and SerializeToUtf8Bytes (which wont happen)
            Debug.Assert(!IsSingleByteMessage || (Unsafe.SizeOf<TMessage>() == 1 && typeof(TMessage).IsValueType));
            var message = IsSingleByteMessage
                ? [Unsafe.BitCast<TMessage, byte>(createMsgToPrimary())]
                : JsonSerializer.SerializeToUtf8Bytes(createMsgToPrimary(), _jsonOptions);
#pragma warning restore Ex0100

            const int OneMiB = 1024 * 1024;
            if (message.Length > OneMiB) throw new ArgumentException($"Notification message is too large. Maximum length is 1,048,576 bytes (1MiB). Message length is {message.Length} bytes.", nameof(createMsgToPrimary));

            var totalAttempts = _options.NotificationRetryPolicy.RetryAttempts + 1;
            for (var attempt = 0; attempt < totalAttempts && !ct.IsCancellationRequested; attempt++) // TODO: maybe dont break (remove the !IsCancellationRequested from the for loop) so that we dont log "failed to notify".
            {
                var success = NotifyPipe(message, _options.NotificationRetryPolicy.ConnectionTimeout);
                if (success) return;

                // do not wait after the last attempt.
                if (attempt == totalAttempts - 1) break;

                if (_options.NotificationRetryPolicy.MaxJitterDelay <= TimeSpan.Zero) continue;

                try
                {
                    var delay = Random.Shared.NextDouble() * _options.NotificationRetryPolicy.MaxJitterDelay;
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

            if (IsSingleByteMessage)
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

            client.Flush();
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
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, true)) return;

        // Acquire _pipeCtsLock to ensure we don't interleave with a concurrent
        // RunServerLoop or NotifyExistingInstance that has passed the outer _disposed check
        // but hasn't yet created its CancellationTokenSource.
#if INCLUDE_TEST_HOOKS
        OnBeforeDisposeCtsLock?.Invoke();
#endif
        CancellationTokenSource? cts;
        lock (_pipeCtsLock)
        {
            cts = Interlocked.Exchange(ref _pipeCts, null);
        }

        if (cts is not null)
        {
            try { cts.Cancel(); } catch { }
            cts.Dispose();
        }

        DisposeCore();
    }

    protected abstract void DisposeCore();
}
