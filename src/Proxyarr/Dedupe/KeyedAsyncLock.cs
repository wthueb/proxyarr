namespace Proxyarr.Dedupe;

/// <summary>
/// Serializes async work by string key. Concurrent adds/deletes of the same item (keyed by
/// <c>{group}|{hash-or-content-key}</c>) run one at a time, which closes the race where two *arr
/// instances grab the same release simultaneously; work on different keys stays fully parallel.
///
/// Each key maps to a ref-counted <see cref="SemaphoreSlim"/> evicted once the last waiter releases,
/// so the dictionary doesn't grow without bound. The short <c>lock</c> only guards the dictionary
/// bookkeeping — never the semaphore wait — so holders on different keys never block each other.
/// </summary>
public sealed class KeyedAsyncLock
{
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public async ValueTask<Releaser> AcquireAsync(
        string key,
        CancellationToken cancellationToken = default
    )
    {
        Entry entry;
        lock (_entries)
        {
            if (!_entries.TryGetValue(key, out entry!))
            {
                entry = new Entry();
                _entries[key] = entry;
            }

            entry.RefCount++;
        }

        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken);
        }
        catch
        {
            Release(key, entry);
            throw;
        }

        return new Releaser(this, key, entry);
    }

    private void Release(string key, Entry entry)
    {
        lock (_entries)
        {
            if (--entry.RefCount == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    internal sealed class Entry
    {
        public readonly SemaphoreSlim Semaphore = new(1, 1);

        public int RefCount;
    }

    public readonly struct Releaser : IDisposable
    {
        private readonly KeyedAsyncLock _owner;
        private readonly string _key;
        private readonly Entry _entry;

        internal Releaser(KeyedAsyncLock owner, string key, Entry entry)
        {
            _owner = owner;
            _key = key;
            _entry = entry;
        }

        public void Dispose()
        {
            _entry.Semaphore.Release();
            _owner.Release(_key, _entry);
        }
    }
}
