<section id="asyncbatcher-overview">
  <h1>AsyncBatcher</h1>

  ![Status](https://img.shields.io/badge/status-stable-blue)
  ![Build](https://img.shields.io/badge/build-passing-brightgreen)
  ![License](https://img.shields.io/badge/license-MIT-lightgrey)

  <h2>Overview</h2>
  <p>
    <strong>AsyncBatcher</strong> is a single-file C# utility that coalesces many small async calls
    into <em>efficient bulk operations</em>. You keep calling a simple per-item API (e.g., <code>GetById</code>);
    the batcher silently groups nearby calls (by time/size), executes one bulk function
    (e.g., <code>GetByIds</code>), and fans results back to the original callers -- with
    backpressure, timeouts, and per-item error isolation.
  </p>

  <p>
    This is ideal for solving N+1 problems across databases, HTTP APIs with bulk endpoints,
    caching layers, and microservices under high concurrency.
  </p>

  <h2>Developer Note</h2>
  <p>
    Part of a personal cleanup of my playground -- a set of small, focused utilities that demonstrate
    clean design and practical problem-solving in production-flavored code. Drop-in, no dependencies.
  </p>

  <h2>Key Features</h2>
  <ul>
    <li><strong>Smart Coalescing:</strong> batches by <em>size</em> and <em>time window</em></li>
    <li><strong>Fan-in / Fan-out:</strong> maps bulk results back to each original input</li>
    <li><strong>Backpressure:</strong> bounded channel prevents runaway memory</li>
    <li><strong>Timeouts & Cancellation:</strong> batch-level timeout, clean shutdown</li>
    <li><strong>Per-item Results:</strong> each caller gets its own Task/exception</li>
    <li><strong>No Dependencies:</strong> pure C#, .NET 8+, one file</li>
  </ul>

  <h2>How It Works</h2>
  <ol>
    <li>Call <code>EnqueueAsync(input)</code> from anywhere -- it returns a Task for that item’s result.</li>
    <li>A background pump collects items until <code>MaxBatchSize</code> or <code>MaxBatchDelay</code> is hit.</li>
    <li>It invokes your <code>bulkFunc(inputs)</code> once.</li>
    <li>Results are matched back to each input via <code>resultSelector</code>, and each Task completes.</li>
  </ol>

  <h2>Example Usage</h2>
  <pre>
    // Suppose your API supports a bulk fetch: GetUsersAsync(ids)
    var batcher = new Demo.AsyncUtils.AsyncBatcher&lt;string, User&gt;(
    bulkFunc: async (ids, ct) =&gt; await api.GetUsersAsync(ids, ct),
    resultSelector: (user, id) =&gt; user.Id == id,
    options: new AsimFaiaz.AsyncUtils.AsyncBatcherOptions
    {
        MaxBatchSize = 128,
        MaxBatchDelay = TimeSpan.FromMilliseconds(15),
        BatchTimeout = TimeSpan.FromSeconds(5),
        ChannelCapacity = 10_000
    }
);

// Anywhere in your code (even concurrently):
var u1Task = batcher.EnqueueAsync&lt;User&gt;("u123");
var u2Task = batcher.EnqueueAsync&lt;User&gt;("u456");

// These will likely coalesce into one bulk call.
var u1 = await u1Task;
var u2 = await u2Task;
  </pre>

  <h2>Interface Summary</h2>
  <pre>
// AsyncBatcher.cs (single-file)
public sealed class AsyncBatcher&lt;TIn, TBulkItem&gt; : IAsyncDisposable
{
    public AsyncBatcher(
        Func&lt;IReadOnlyList&lt;TIn&gt;, CancellationToken, Task&lt;IReadOnlyList&lt;TBulkItem&gt;&gt;&gt; bulkFunc,
        Func&lt;TBulkItem, TIn, bool&gt; resultSelector,
        AsyncBatcherOptions? options = null
    );

    // Enqueue an input and await its individual result (type-param of EnqueueAsync)
    public Task&lt;TOut&gt; EnqueueAsync&lt;TOut&gt;(TIn input, CancellationToken ct = default);

    public ValueTask DisposeAsync();
}

public sealed class AsyncBatcherOptions
{
    public int MaxBatchSize { get; init; }          // default 100
    public TimeSpan MaxBatchDelay { get; init; }    // default 10ms
    public TimeSpan BatchTimeout { get; init; }     // default 10s
    public int ChannelCapacity { get; init; }       // default 10_000
}
  </pre>

  <h2>Why AsyncBatcher?</h2>
  <p>
    Batching sounds simple until you hit edge cases under load. Here are the common pitfalls and how
    <strong>AsyncBatcher</strong> addresses them:
  </p>

  <table>
    <thead>
      <tr><th>Problem</th><th>What goes wrong</th><th>How AsyncBatcher helps</th></tr>
    </thead>
    <tbody>
      <tr>
        <td><strong>N+1 / Chatty calls</strong></td>
        <td>Hundreds of individual calls crush latency and rate limits</td>
        <td>Batches by size/time; one bulk call serves many</td>
      </tr>
      <tr>
        <td><strong>Mapping results</strong></td>
        <td>Bulk endpoints return out-of-order / missing items</td>
        <td><code>resultSelector</code> maps each input to its own result; per-item errors</td>
      </tr>
      <tr>
        <td><strong>Timing window</strong></td>
        <td>Too short = no batching; too long = visible lag</td>
        <td><code>MaxBatchDelay</code> balances throughput and latency</td>
      </tr>
      <tr>
        <td><strong>Backpressure</strong></td>
        <td>Unbounded queues = memory blowups under spikes</td>
        <td>Bounded <code>Channel</code> with producers awaiting when full</td>
      </tr>
      <tr>
        <td><strong>Partial failures</strong></td>
        <td>One bad id ruins the batch for everyone</td>
        <td>Per-item exceptions; good results still complete</td>
      </tr>
      <tr>
        <td><strong>Timeouts & cancellation</strong></td>
        <td>Hung bulk calls block the system</td>
        <td>Batch-level timeout + clean cancellation/teardown</td>
      </tr>
    </tbody>
  </table>

  <h2>Configuration</h2>
  <pre>
new AsyncBatcherOptions
{
    MaxBatchSize   = 100,                      // max items per batch
    MaxBatchDelay  = TimeSpan.FromMilliseconds(10), // wait window to coalesce
    BatchTimeout   = TimeSpan.FromSeconds(10), // max time bulkFunc may run
    ChannelCapacity = 10_000                   // backpressure bound
};
  </pre>

  <h2>Manual Testing - .NET Fiddle</h2>
  <pre>
//To Test in .NET Fiddle simply paste this code under the main CS code
public static class Program
    {
        public static async Task Main()
        {
            Console.WriteLine("Starting AsyncBatcher demo...\n");

            async Task<IReadOnlyList<string>> BulkUppercase(IReadOnlyList<string> inputs, CancellationToken ct)
            {
                Console.WriteLine($"Bulk called for [{string.Join(", ", inputs)}] (count={inputs.Count})");
                await Task.Delay(500, ct); 
                return inputs.Select(s => s.ToUpperInvariant()).ToArray();
            }

            using var batcher = new AsyncBatcher<string, string>(
                BulkUppercase,
                (bulk, input) => bulk.Equals(input, StringComparison.OrdinalIgnoreCase),
                new AsyncBatcherOptions { MaxBatchSize = 4, MaxBatchDelay = TimeSpan.FromMilliseconds(200) }
            );

            var tasks = new List<Task>();

            for (int i = 1; i <= 10; i++)
            {
                var word = "word" + i;
                tasks.Add(Task.Run(async () =>
                {
                    var result = await batcher.EnqueueAsync<string>(word);
                    Console.WriteLine($"Result for {word} → {result}");
                }));

                await Task.Delay(i % 3 == 0 ? 250 : 50);
            }

            await Task.WhenAll(tasks);
        }
    }
  </pre>

  <h2>Use Cases</h2>
  <ul>
    <li><strong>Database:</strong> replace loops with <code>IN (...)</code> bulk SELECTs</li>
    <li><strong>HTTP APIs:</strong> turn many <code>/users/{id}</code> calls into one <code>/users/bulk?ids=...</code></li>
    <li><strong>Caching layers:</strong> batch cache misses to reduce backend pressure</li>
    <li><strong>Microservices:</strong> coalesce hot-path lookups without changing callers</li>
  </ul>

  <h2>Notes & Tips</h2>
  <ul>
    <li>Choose <code>MaxBatchDelay</code> small enough to keep p99 latency tight (e.g., 5–20ms).</li>
    <li>Large batch payloads may increase memory usage -- tune <code>MaxBatchSize</code>.</li>
    <li><code>resultSelector</code> usually compares keys (e.g., <code>item.Id == key</code>).</li>
    <li>For multi-tenant apps, consider running a separate batcher per key/tenant if needed.</li>
  </ul>

  <section id="tech-stack">
    <h2>Tech Stack</h2>
    <pre>☑ C# (.NET 8 or newer)</pre>
    <pre>☑ System.Threading.Channels</pre>
    <pre>☑ No external dependencies</pre>
  </section>

  <h2>Build Status</h2>
  <p>
    This repo is a single-file demonstration and does not include a build pipeline.  
    Future updates may add automated tests and GitHub Actions workflows.
  </p>

  <h2>License</h2>
  <p>
    Licensed under the <a href="LICENSE">MIT License</a>.<br>
  </p>
</section>
