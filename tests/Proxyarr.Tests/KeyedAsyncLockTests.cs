using Proxyarr.Dedupe;

namespace Proxyarr.Tests;

public class KeyedAsyncLockTests
{
    [Fact]
    public async Task Same_key_runs_sequentially()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = new KeyedAsyncLock();
        var concurrent = 0;
        var maxObserved = 0;

        async Task Work()
        {
            using (await locks.AcquireAsync("k", ct))
            {
                var now = Interlocked.Increment(ref concurrent);
                maxObserved = Math.Max(maxObserved, now);
                await Task.Delay(20, ct);
                Interlocked.Decrement(ref concurrent);
            }
        }

        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Work()));

        Assert.Equal(1, maxObserved);
    }

    [Fact]
    public async Task Different_keys_run_concurrently()
    {
        var locks = new KeyedAsyncLock();
        using var barrier = new SemaphoreSlim(0, 2);

        var ct = TestContext.Current.CancellationToken;
        var first = Enter("a");
        var second = Enter("b");

        // Both should be able to enter their critical sections at once; if the lock were global one
        // of these would never release the barrier and the wait would time out.
        var released = await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.Equal([true, true], released);

        async Task<bool> Enter(string key)
        {
            using (await locks.AcquireAsync(key, ct))
            {
                barrier.Release();
                return await barrier.WaitAsync(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    [Fact]
    public async Task Lock_is_reusable_after_release_without_leaking_entries()
    {
        var ct = TestContext.Current.CancellationToken;
        var locks = new KeyedAsyncLock();

        for (var i = 0; i < 100; i++)
        {
            using (await locks.AcquireAsync("same", ct))
            {
                // no-op
            }
        }

        // A fresh acquire still completes promptly (no deadlock from a stuck refcount/semaphore).
        using var final = await locks
            .AcquireAsync("same", ct)
            .AsTask()
            .WaitAsync(TimeSpan.FromSeconds(2), ct);
    }
}
