public class QuoteServiceTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<QuoteService> ServiceAsync(params string[] quotes) =>
        new(await Db.WithQuotesAsync(_dir.File("quotes.json"), quotes));

    [Fact]
    public async Task HashOf_IsTenUppercaseHexChars()
    {
        var quotes = await ServiceAsync();

        Assert.Matches("^[0-9A-F]{10}$", quotes.HashOf("Волк слабее льва и тигра"));
    }

    [Fact]
    public async Task HashOf_IgnoresCaseAndSurroundingWhitespace()
    {
        var quotes = await ServiceAsync();

        Assert.Equal(quotes.HashOf("Вълкъ мудръ"), quotes.HashOf("  вЪЛКЪ МУДРЪ \n"));
        Assert.NotEqual(quotes.HashOf("Вълкъ мудръ"), quotes.HashOf("Вълкъ не мудръ"));
    }

    [Fact]
    public async Task HashOf_IsStableAcrossVersions()
    {
        // Хеш лежит в ratings и в callback-данных уже отправленных сообщений:
        // если он изменится, все голоса и старые кнопки потеряются
        var quotes = await ServiceAsync();

        Assert.Equal("E42264DAA1", quotes.HashOf("Волк"));
    }

    [Fact]
    public async Task HashOf_FitsTelegramCallbackLimit()
    {
        var quotes = await ServiceAsync();
        var hash = quotes.HashOf(new string('я', 5000));

        // Самый длинный префикс callback-данных в BotService — "dislike:"
        Assert.True(System.Text.Encoding.UTF8.GetByteCount("dislike:" + hash) <= 64);
    }

    [Fact]
    public async Task Exists_IsCaseAndWhitespaceInsensitive()
    {
        var quotes = await ServiceAsync("Лучше быть последним среди волков");

        Assert.True(quotes.Exists("  лучше быть ПОСЛЕДНИМ среди волков "));
        Assert.False(quotes.Exists("Лучше быть первым среди овец"));
    }

    [Fact]
    public async Task GetRandomQuote_EmptyBase_ReturnsNull()
    {
        var quotes = await ServiceAsync();

        Assert.Null(quotes.GetRandomQuote());
    }

    [Fact]
    public async Task GetRandomQuote_ReturnsQuoteFromBase()
    {
        string[] all = ["Раз", "Два", "Три"];
        var quotes = await ServiceAsync(all);

        for (var i = 0; i < 20; i++)
            Assert.Contains(quotes.GetRandomQuote(), all);
    }

    [Fact]
    public async Task Vote_UnknownHash_ReturnsNullAndStoresNothing()
    {
        var quotes = await ServiceAsync("Цитата");

        Assert.Null(quotes.Vote("0000000000", 1, like: true));
        Assert.Equal((0, 0), quotes.GetCounts("0000000000"));
    }

    [Fact]
    public async Task Vote_SecondSameVote_RemovesIt()
    {
        var quotes = await ServiceAsync("Цитата");
        var hash = quotes.HashOf("Цитата");

        Assert.Equal((1, 0), quotes.Vote(hash, 1, like: true));
        Assert.Equal((0, 0), quotes.Vote(hash, 1, like: true));
    }

    [Fact]
    public async Task Vote_OppositeVote_MovesUser()
    {
        var quotes = await ServiceAsync("Цитата");
        var hash = quotes.HashOf("Цитата");

        quotes.Vote(hash, 1, like: true);

        Assert.Equal((0, 1), quotes.Vote(hash, 1, like: false));
        Assert.Equal((0, 1), quotes.GetCounts(hash));
    }

    [Fact]
    public async Task Vote_CountsDifferentUsersSeparately()
    {
        var quotes = await ServiceAsync("Цитата");
        var hash = quotes.HashOf("Цитата");

        quotes.Vote(hash, 1, like: true);
        quotes.Vote(hash, 2, like: true);
        quotes.Vote(hash, 3, like: false);

        Assert.Equal((2, 1), quotes.GetCounts(hash));
    }

    [Fact]
    public async Task Vote_IsPersistedToFile()
    {
        var file = _dir.File("quotes.json");
        var quotes = new QuoteService(await Db.WithQuotesAsync(file, "Цитата"));
        var hash = quotes.HashOf("Цитата");
        quotes.Vote(hash, 42, like: true);

        var reloaded = new QuoteService(await Db.LocalAsync(file));

        Assert.Equal((1, 0), reloaded.GetCounts(hash));
    }

    [Fact]
    public async Task Vote_ConcurrentVotes_AreAllCounted()
    {
        var quotes = await ServiceAsync("Цитата");
        var hash = quotes.HashOf("Цитата");

        await Task.WhenAll(Enumerable.Range(1, 100)
            .Select(userId => Task.Run(() => quotes.Vote(hash, userId, like: userId % 2 == 0))));

        Assert.Equal((50, 50), quotes.GetCounts(hash));
    }
}
