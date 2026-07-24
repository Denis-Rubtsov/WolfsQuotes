class RateLimiter
{
    private readonly int _limit;
    private readonly TimeSpan _window;
    private readonly object _lock = new();
    private readonly Dictionary<long, List<DateTime>> _hits = new();

    public RateLimiter(int limit, TimeSpan window)
    {
        _limit = limit;
        _window = window;
    }

    public bool TryAcquire(long userId)
    {
        lock (_lock)
        {
            var now = DateTime.UtcNow;

            if (_hits.Count > 500) Sweep(now);

            if (!_hits.TryGetValue(userId, out var hits))
            {
                hits = new List<DateTime>();
                _hits[userId] = hits;
            }

            hits.RemoveAll(t => now - t > _window);

            if (hits.Count >= _limit) return false;

            hits.Add(now);
            return true;
        }
    }

    private void Sweep(DateTime now)
    {
        var stale = _hits
            .Where(kv => kv.Value.All(t => now - t > _window))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in stale)
            _hits.Remove(key);
    }
}
