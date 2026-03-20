using System.Collections.Concurrent;

namespace bsckend.Services;

public interface IWopiLockService
{
    bool TryLock(string fileId, string lockValue, out string? existingLock);
    bool TryUnlock(string fileId, string lockValue, out string? existingLock);
    bool TryRefreshLock(string fileId, string lockValue, out string? existingLock);
    bool TryGetLock(string fileId, out string? lockValue);
    bool IsLockMatching(string fileId, string lockValue, out string? existingLock);
}

public class WopiLockService : IWopiLockService
{
    private static readonly TimeSpan LockLifetime = TimeSpan.FromMinutes(30);
    private readonly ConcurrentDictionary<string, LockEntry> _locks = new();

    public bool TryLock(string fileId, string lockValue, out string? existingLock)
    {
        existingLock = null;
        CleanupIfExpired(fileId);

        if (_locks.TryGetValue(fileId, out var current))
        {
            existingLock = current.LockValue;
            if (!string.Equals(current.LockValue, lockValue, StringComparison.Ordinal))
            {
                return false;
            }

            _locks[fileId] = current.Refresh();
            return true;
        }

        _locks[fileId] = new LockEntry(lockValue, DateTime.UtcNow.Add(LockLifetime));
        return true;
    }

    public bool TryUnlock(string fileId, string lockValue, out string? existingLock)
    {
        existingLock = null;
        CleanupIfExpired(fileId);

        if (!_locks.TryGetValue(fileId, out var current))
        {
            return true;
        }

        existingLock = current.LockValue;
        if (!string.Equals(current.LockValue, lockValue, StringComparison.Ordinal))
        {
            return false;
        }

        _locks.TryRemove(fileId, out _);
        return true;
    }

    public bool TryRefreshLock(string fileId, string lockValue, out string? existingLock)
    {
        existingLock = null;
        CleanupIfExpired(fileId);

        if (!_locks.TryGetValue(fileId, out var current))
        {
            return false;
        }

        existingLock = current.LockValue;
        if (!string.Equals(current.LockValue, lockValue, StringComparison.Ordinal))
        {
            return false;
        }

        _locks[fileId] = current.Refresh();
        return true;
    }

    public bool TryGetLock(string fileId, out string? lockValue)
    {
        CleanupIfExpired(fileId);

        if (_locks.TryGetValue(fileId, out var current))
        {
            lockValue = current.LockValue;
            return true;
        }

        lockValue = null;
        return false;
    }

    public bool IsLockMatching(string fileId, string lockValue, out string? existingLock)
    {
        CleanupIfExpired(fileId);

        if (!_locks.TryGetValue(fileId, out var current))
        {
            existingLock = null;
            return true;
        }

        existingLock = current.LockValue;
        return string.Equals(current.LockValue, lockValue, StringComparison.Ordinal);
    }

    private void CleanupIfExpired(string fileId)
    {
        if (_locks.TryGetValue(fileId, out var current) && current.ExpiresAtUtc <= DateTime.UtcNow)
        {
            _locks.TryRemove(fileId, out _);
        }
    }

    private sealed record LockEntry(string LockValue, DateTime ExpiresAtUtc)
    {
        public LockEntry Refresh() => this with { ExpiresAtUtc = DateTime.UtcNow.Add(LockLifetime) };
    }
}

