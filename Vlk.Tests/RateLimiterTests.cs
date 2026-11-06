public class RateLimiterTests
{
    [Fact]
    public void AllowsUpToLimitThenRejects()
    {
        var limiter = new RateLimiter(5, TimeSpan.FromHours(1));

        for (var i = 0; i < 5; i++)
            Assert.True(limiter.TryAcquire(1));

        Assert.False(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));
    }

    [Fact]
    public void LimitsAreTrackedPerUser()
    {
        var limiter = new RateLimiter(1, TimeSpan.FromHours(1));

        Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));
        Assert.True(limiter.TryAcquire(2));
    }

    [Fact]
    public async Task HitsExpireAfterWindow()
    {
        var limiter = new RateLimiter(1, TimeSpan.FromMilliseconds(200));

        Assert.True(limiter.TryAcquire(1));
        Assert.False(limiter.TryAcquire(1));

        await Task.Delay(400, TestContext.Current.CancellationToken);

        Assert.True(limiter.TryAcquire(1));
    }

    [Fact]
    public async Task SweepOfManyUsers_KeepsActiveLimits()
    {
        // После 500 пользователей срабатывает очистка; она не должна сбрасывать живые счётчики
        var limiter = new RateLimiter(1, TimeSpan.FromMilliseconds(300));
        Assert.True(limiter.TryAcquire(-1));

        for (var id = 0; id < 600; id++)
            limiter.TryAcquire(id);

        Assert.False(limiter.TryAcquire(-1));
        Assert.False(limiter.TryAcquire(599));

        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.True(limiter.TryAcquire(-1));
    }

    [Fact]
    public async Task ConcurrentAcquires_NeverExceedLimit()
    {
        var limiter = new RateLimiter(5, TimeSpan.FromHours(1));

        var results = await Task.WhenAll(Enumerable.Range(0, 200)
            .Select(_ => Task.Run(() => limiter.TryAcquire(7))));

        Assert.Equal(5, results.Count(ok => ok));
    }
}
