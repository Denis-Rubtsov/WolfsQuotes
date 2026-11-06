using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

public class DataServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private string DbFile => _dir.File("quotes.json");

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Load_WithoutFileAndS3_Throws()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DataService.LoadAsync("  ", null, "quotes.json", 0, NullLogger.Instance));
    }

    [Fact]
    public async Task Load_MissingFile_StartsEmptyWithoutCreatingIt()
    {
        var data = await Db.LocalAsync(DbFile);

        Assert.Empty(data.Data.quotes);
        Assert.False(File.Exists(DbFile));
    }

    [Fact]
    public async Task Load_ReadsExistingFile()
    {
        await File.WriteAllTextAsync(DbFile, """
            {
              "quotes": ["Первая", "Вторая"],
              "allow_public_generation": true,
              "paid_generations": { "42": 3 },
              "stars_price": 25
            }
            """, TestContext.Current.CancellationToken);

        var data = await Db.LocalAsync(DbFile);

        Assert.Equal(["Первая", "Вторая"], data.Data.quotes);
        Assert.True(data.AllowPublicGeneration);
        Assert.Equal(25, data.StarsPrice(10));
        Assert.True(data.TryConsumePaidGeneration(42));
    }

    [Fact]
    public async Task Save_RoundTripsAllFields()
    {
        var data = await Db.WithQuotesAsync(DbFile, "Цитата");
        data.AddSuggestion(new Suggestion { user_id = 5, name = "Тест", quote = "Предложение" });
        data.AddPaidGenerations(5, 2);
        data.SetStarsPrice(7);
        data.TogglePublicGeneration();

        var reloaded = await Db.LocalAsync(DbFile);

        Assert.Equal(["Цитата"], reloaded.Data.quotes);
        var suggestion = Assert.Single(reloaded.Data.suggestions);
        Assert.Equal((5L, "Тест", "Предложение"), (suggestion.user_id, suggestion.name, suggestion.quote));
        Assert.Equal(2, reloaded.Data.paid_generations[5]);
        Assert.Equal(7, reloaded.StarsPrice(10));
        Assert.True(reloaded.AllowPublicGeneration);
        Assert.NotNull(reloaded.Data.updated_at);
    }

    [Fact]
    public async Task Save_StampsUpdatedAt()
    {
        var data = await Db.LocalAsync(DbFile);
        var before = DateTimeOffset.UtcNow;

        data.TogglePublicGeneration();

        Assert.InRange(data.Data.updated_at!.Value, before, DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task Save_KeepsCyrillicReadableAndLeavesNoTmpFile()
    {
        var data = await Db.WithQuotesAsync(DbFile, "Вълкъ");

        var json = await File.ReadAllTextAsync(DbFile, TestContext.Current.CancellationToken);
        Assert.Contains("Вълкъ", json);
        Assert.DoesNotContain("\\u", json);
        Assert.False(File.Exists(DbFile + ".tmp"));
        // Файл — валидный JSON, который читается обратно
        Assert.NotNull(JsonSerializer.Deserialize<BotData>(json));
    }

    [Fact]
    public async Task StarsPrice_FallsBackToConfigUntilSet()
    {
        var data = await Db.LocalAsync(DbFile);

        Assert.Equal(10, data.StarsPrice(10));
        data.SetStarsPrice(0);
        Assert.Equal(0, data.StarsPrice(10));
    }

    [Fact]
    public async Task TogglePublicGeneration_Flips()
    {
        var data = await Db.LocalAsync(DbFile);

        Assert.True(data.TogglePublicGeneration());
        Assert.False(data.TogglePublicGeneration());
        Assert.False(data.AllowPublicGeneration);
    }

    [Fact]
    public async Task PaidGenerations_ConsumeUntilEmpty()
    {
        var data = await Db.LocalAsync(DbFile);

        Assert.False(data.TryConsumePaidGeneration(1));

        data.AddPaidGenerations(1, 2);
        Assert.True(data.TryConsumePaidGeneration(1));
        Assert.True(data.TryConsumePaidGeneration(1));
        Assert.False(data.TryConsumePaidGeneration(1));
        Assert.Equal(0, data.Data.paid_generations[1]);
    }

    [Fact]
    public async Task PaidGenerations_ConcurrentConsumption_NeverGoesNegative()
    {
        var data = await Db.LocalAsync(DbFile);
        data.AddPaidGenerations(1, 10);

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => data.TryConsumePaidGeneration(1))));

        Assert.Equal(10, results.Count(ok => ok));
        Assert.Equal(0, data.Data.paid_generations[1]);
    }

    [Fact]
    public async Task TryAddQuote_RespectsExistsCheck()
    {
        var data = await Db.LocalAsync(DbFile);
        var quotes = new QuoteService(data);

        Assert.True(data.TryAddQuote("Цитата", quotes.Exists));
        Assert.False(data.TryAddQuote(" цитата ", quotes.Exists));
        Assert.Single(data.Data.quotes);
    }

    [Fact]
    public async Task TryAddQuote_ConcurrentDuplicates_AddedOnce()
    {
        var data = await Db.LocalAsync(DbFile);
        var quotes = new QuoteService(data);

        await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Task.Run(() => data.TryAddQuote("Одна и та же", quotes.Exists))));

        Assert.Single(data.Data.quotes);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    public async Task EditAndRemove_OutOfRange_DoNothing(int index)
    {
        var data = await Db.WithQuotesAsync(DbFile, "А", "Б");

        Assert.False(data.TryEditQuoteAt(index, "В"));
        Assert.Null(data.TryRemoveQuoteAt(index));
        Assert.Equal(["А", "Б"], data.Data.quotes);
    }

    [Fact]
    public async Task EditQuote_ReplacesText()
    {
        var data = await Db.WithQuotesAsync(DbFile, "А", "Б");

        Assert.True(data.TryEditQuoteAt(1, "В"));

        Assert.Equal(["А", "В"], (await Db.LocalAsync(DbFile)).Data.quotes);
    }

    [Fact]
    public async Task RemoveQuote_ReturnsRemovedText()
    {
        var data = await Db.WithQuotesAsync(DbFile, "А", "Б", "В");

        Assert.Equal("Б", data.TryRemoveQuoteAt(1));

        Assert.Equal(["А", "В"], (await Db.LocalAsync(DbFile)).Data.quotes);
    }

    [Fact]
    public async Task RemoveSuggestion_Approve_AddsQuote()
    {
        var data = await Db.LocalAsync(DbFile);
        var quotes = new QuoteService(data);
        var suggestion = new Suggestion { quote = "Новая" };
        data.AddSuggestion(suggestion);

        var removed = data.RemoveSuggestion(l => l.FindIndex(s => s.id == suggestion.id), approve: true, quotes.Exists);

        Assert.Same(suggestion, removed);
        Assert.Empty(data.Data.suggestions);
        Assert.Equal(["Новая"], data.Data.quotes);
    }

    [Fact]
    public async Task RemoveSuggestion_ApproveDuplicate_DoesNotDuplicateQuote()
    {
        var data = await Db.WithQuotesAsync(DbFile, "Уже есть");
        var quotes = new QuoteService(data);
        data.AddSuggestion(new Suggestion { quote = "уже есть" });

        Assert.NotNull(data.RemoveSuggestion(_ => 0, approve: true, quotes.Exists));

        Assert.Equal(["Уже есть"], data.Data.quotes);
        Assert.Empty(data.Data.suggestions);
    }

    [Fact]
    public async Task RemoveSuggestion_Reject_DropsWithoutAdding()
    {
        var data = await Db.LocalAsync(DbFile);
        data.AddSuggestion(new Suggestion { quote = "Плохая" });

        Assert.NotNull(data.RemoveSuggestion(_ => 0, approve: false, _ => false));

        Assert.Empty(data.Data.quotes);
        Assert.Empty(data.Data.suggestions);
    }

    [Fact]
    public async Task RemoveSuggestion_NotFound_ReturnsNull()
    {
        var data = await Db.LocalAsync(DbFile);
        data.AddSuggestion(new Suggestion { quote = "Есть" });

        Assert.Null(data.RemoveSuggestion(l => l.FindIndex(s => s.id == "нет такого"), approve: true, _ => false));
        Assert.Single(data.Data.suggestions);
    }

    [Fact]
    public async Task Suggestion_GetsUniqueIds()
    {
        Assert.NotEqual(new Suggestion().id, new Suggestion().id);
        Assert.Matches("^[0-9a-f]{32}$", new Suggestion().id);
    }

    [Fact]
    public async Task FlushAsync_WithoutS3_IsNoOp()
    {
        var data = await Db.WithQuotesAsync(DbFile, "А");

        await data.FlushAsync(TimeSpan.FromSeconds(1));
    }
}
