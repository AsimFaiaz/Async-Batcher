// AsyncBatcher.cs
// Coalesce many small async calls into efficient bulk operations
// Author: Asim Faiaz 
// License: MIT

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Demo.AsyncUtils
{
    /*
     * Batches inputs arriving around the same time into a single bulk operation.
		- Call EnqueueAsync(item, ct) to schedule work; returns the per-item result Task.
		- Internally collects up to MaxBatchSize or waits MaxBatchDelay, then invokes the provided bulkFunc.
		- Maps per-item results back to callers using a user-provided resultSelector.

    * Guarantees:
		- Each call completes with its own result or throws if not found / bulk failed.
		- Batching window is bounded by MaxBatchDelay or MaxBatchSize, whichever hits first.
		- Backpressure-friendly: input channel is bounded to prevent unbounded memory growth (configurable).
    */
    public sealed class AsyncBatcher<TIn, TBulkItem> : IAsyncDisposable
    {
        private readonly Func<IReadOnlyList<TIn>, CancellationToken, Task<IReadOnlyList<TBulkItem>>> _bulkFunc;
        private readonly Func<TBulkItem, TIn, bool> _resultSelector;
        private readonly AsyncBatcherOptions _opt;
        private readonly Channel<Enqueued> _channel;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _pumpTask;

        private readonly struct Enqueued
        {
            public readonly TIn Input;
            public readonly TaskCompletionSource<object?> Tcs;
            public Enqueued(TIn input, TaskCompletionSource<object?> tcs) { Input = input; Tcs = tcs; }
        }

        public AsyncBatcher(
            Func<IReadOnlyList<TIn>, CancellationToken, Task<IReadOnlyList<TBulkItem>>> bulkFunc,
            Func<TBulkItem, TIn, bool> resultSelector,
            AsyncBatcherOptions? options = null)
        {
            _bulkFunc = bulkFunc ?? throw new ArgumentNullException(nameof(bulkFunc));
            _resultSelector = resultSelector ?? throw new ArgumentNullException(nameof(resultSelector));
            _opt = options ?? new AsyncBatcherOptions();

            var chOpt = new BoundedChannelOptions(_opt.ChannelCapacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait
            };
            _channel = Channel.CreateBounded<Enqueued>(chOpt);
            _pumpTask = Task.Run(PumpAsync);
        }

        /*
			- Enqueue a single input for batched execution. Returns a Task for the per-item result.
			- If a per-item result cannot be matched, throws KeyNotFoundException.
        */
        public async Task<TOut> EnqueueAsync<TOut>(TIn input, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var enq = new Enqueued(input, tcs);

            // Respect cancellation on enqueue
            using var reg = ct.Register(() => tcs.TrySetCanceled(ct));
            await _channel.Writer.WriteAsync(enq, ct).ConfigureAwait(false);

            var obj = await tcs.Task.ConfigureAwait(false);
            return (TOut)obj!;
        }

        private async Task PumpAsync()
        {
            var reader = _channel.Reader;

            var batch = new List<Enqueued>(_opt.MaxBatchSize);
            var delayCts = (CancellationTokenSource?)null;

            try
            {
                while (await reader.WaitToReadAsync(_cts.Token).ConfigureAwait(false))
                {
                    // Start new batch window if needed
                    batch.Clear();
                    delayCts?.Dispose();
                    delayCts = new CancellationTokenSource();

                    // read at least one item
                    if (!reader.TryRead(out var first)) continue;
                    batch.Add(first);

                    // Start delay clock
                    var delayTask = Task.Delay(_opt.MaxBatchDelay, delayCts.Token);

                    // Keep collecting until size reached or delay elapsed
                    while (batch.Count < _opt.MaxBatchSize)
                    {
                        if (reader.TryRead(out var next))
                        {
                            batch.Add(next);
                            continue;
                        }

                        var readTask = reader.ReadAsync(_cts.Token).AsTask();
                        var completed = await Task.WhenAny(readTask, delayTask).ConfigureAwait(false);

                        if (completed == delayTask)
                            break; // time window elapsed

                        // got another item
                        batch.Add(await readTask.ConfigureAwait(false));
                    }

                    // Stop the delay
                    delayCts.Cancel();

                    // Execute this batch
                    _ = ExecuteBatchAsync(batch);
                }
            }
            catch (OperationCanceledException) { /* shutting down */ }
            finally
            {
                delayCts?.Dispose();
            }
        }

        private async Task ExecuteBatchAsync(List<Enqueued> batch)
        {
            if (batch.Count == 0) return;

            // Prepare inputs and linked CTS for batch timeout
            var inputs = batch.Select(b => b.Input).ToArray();
            using var timeoutCts = new CancellationTokenSource(_opt.BatchTimeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, _cts.Token);

            try
            {
                var bulk = await _bulkFunc(inputs, linked.Token).ConfigureAwait(false);
                if (bulk is null) throw new InvalidOperationException("Bulk function returned null.");

                // Build lookup: for each input, find matching bulk item(s)
                // Usually 1:1, but allow first match; you can override matching via _resultSelector.
                foreach (var enq in batch)
                {
                    var match = bulk.FirstOrDefault(b => _resultSelector(b, enq.Input));
                    if (match is null)
                    {
                        enq.Tcs.TrySetException(new KeyNotFoundException($"No result for input '{enq.Input}'."));
                    }
                    else
                    {
                        enq.Tcs.TrySetResult(match!);
                    }
                }
            }
            catch (OperationCanceledException oce)
            {
                var ex = new TimeoutException($"Batch timed out after {_opt.BatchTimeout}.", oce);
                foreach (var enq in batch) enq.Tcs.TrySetException(ex);
            }
            catch (Exception ex)
            {
                foreach (var enq in batch) enq.Tcs.TrySetException(ex);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _channel.Writer.TryComplete();
            _cts.Cancel();
            try { await _pumpTask.ConfigureAwait(false); } catch { /* ignore */ }
            _cts.Dispose();
        }
    }

    // Options for AsyncBatcher: controls windowing and safety.
    public sealed class AsyncBatcherOptions
    {
        // Max items in a single batch
        public int MaxBatchSize { get; init; } = 100;

        // Max time to wait for more items before dispatching a batch
        public TimeSpan MaxBatchDelay { get; init; } = TimeSpan.FromMilliseconds(10);

        / Max time the bulk operation is allowed to run for a batch
        public TimeSpan BatchTimeout { get; init; } = TimeSpan.FromSeconds(10);

        // Channel capacity to bound memory; callers await when full
        public int ChannelCapacity { get; init; } = 10_000;
    }
}
