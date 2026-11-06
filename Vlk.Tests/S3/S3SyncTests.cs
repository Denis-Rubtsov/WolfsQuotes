using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;

[Collection("S3")]
public class S3SyncTests(S3Fixture s3) : IDisposable
{
    private const string Key = "quotes.json";
    private static readonly TimeSpan FlushTimeout = TimeSpan.FromSeconds(20);

    private readonly TempDir _dir = new();
    private string LocalFile => _dir.File("quotes.json");
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private static string Json(DateTimeOffset? updatedAt, params string[] quotes) =>
        JsonSerializer.Serialize(new BotData { quotes = [.. quotes], updated_at = updatedAt });

    private static async Task<BotData?> RemoteAsync(S3Storage storage, string key = Key) =>
        await storage.GetTextAsync(key) is { } json ? JsonSerializer.Deserialize<BotData>(json) : null;

    private Task<DataService> LoadAsync(S3Storage? storage, int backupDays = 0, string? file = null) =>
        DataService.LoadAsync(file ?? LocalFile, storage, Key, backupDays, NullLogger.Instance);

    [Fact]
    public async Task Storage_PutGetExistsCopyDelete()
    {
        var storage = await s3.NewBucketAsync();

        Assert.Null(await storage.GetTextAsync("нет.json", Ct));
        Assert.False(await storage.ExistsAsync("a.txt", Ct));

        await storage.PutTextAsync("a.txt", "Привет, вълкъ", "text/plain", Ct);
        Assert.True(await storage.ExistsAsync("a.txt", Ct));
        Assert.Equal("Привет, вълкъ", await storage.GetTextAsync("a.txt", Ct));

        await storage.CopyAsync("a.txt", "dir/b.txt", Ct);
        Assert.Equal("Привет, вълкъ", await storage.GetTextAsync("dir/b.txt", Ct));
        Assert.Equal(["dir/b.txt"], await storage.ListKeysAsync("dir/", Ct));

        await storage.DeleteAsync("a.txt", Ct);
        Assert.False(await storage.ExistsAsync("a.txt", Ct));
    }

    [Fact]
    public async Task Storage_ConditionalGet_SkipsUnchangedBody()
    {
        var storage = await s3.NewBucketAsync();
        await storage.PutTextAsync("p.txt", "v1", "text/plain", Ct);

        var first = await storage.GetIfChangedAsync("p.txt", null, Ct);
        Assert.Equal((true, false, "v1"), (first.Found, first.NotModified, first.Text));
        Assert.NotNull(first.ETag);

        var same = await storage.GetIfChangedAsync("p.txt", first.ETag, Ct);
        Assert.Equal((true, true, null), (same.Found, same.NotModified, same.Text));

        await storage.PutTextAsync("p.txt", "v2", "text/plain", Ct);
        var changed = await storage.GetIfChangedAsync("p.txt", first.ETag, Ct);
        Assert.Equal((true, false, "v2"), (changed.Found, changed.NotModified, changed.Text));
        Assert.NotEqual(first.ETag, changed.ETag);

        var missing = await storage.GetIfChangedAsync("нет.txt", first.ETag, Ct);
        Assert.False(missing.Found);
    }

    [Fact]
    public async Task Load_EmptyS3_UploadsLocalCopy()
    {
        var storage = await s3.NewBucketAsync();
        await File.WriteAllTextAsync(LocalFile, Json(DateTimeOffset.UtcNow.AddDays(-1), "Локальная"), Ct);

        var data = await LoadAsync(storage);
        Assert.Equal(["Локальная"], data.Data.quotes);

        await data.FlushAsync(FlushTimeout);
        Assert.Equal(["Локальная"], (await RemoteAsync(storage))!.quotes);
    }

    [Fact]
    public async Task Load_EmptyS3AndNoLocal_StartsEmpty()
    {
        var storage = await s3.NewBucketAsync();

        var data = await LoadAsync(storage);
        await data.FlushAsync(FlushTimeout);

        Assert.Empty(data.Data.quotes);
        Assert.Empty((await RemoteAsync(storage))!.quotes);
    }

    [Fact]
    public async Task Load_S3Newer_WinsAndRefreshesLocalFile()
    {
        var storage = await s3.NewBucketAsync();
        var now = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(LocalFile, Json(now.AddHours(-1), "Старая"), Ct);
        await storage.PutTextAsync(Key, Json(now, "Из S3"), "application/json", Ct);

        var data = await LoadAsync(storage);
        await data.FlushAsync(FlushTimeout);

        Assert.Equal(["Из S3"], data.Data.quotes);
        Assert.Equal(["Из S3"], JsonSerializer.Deserialize<BotData>(await File.ReadAllTextAsync(LocalFile, Ct))!.quotes);
    }

    [Fact]
    public async Task Load_LocalNewer_WinsAndOverwritesS3()
    {
        var storage = await s3.NewBucketAsync();
        var now = DateTimeOffset.UtcNow;
        await File.WriteAllTextAsync(LocalFile, Json(now, "Свежая локальная"), Ct);
        await storage.PutTextAsync(Key, Json(now.AddHours(-1), "Старая в S3"), "application/json", Ct);

        var data = await LoadAsync(storage);
        await data.FlushAsync(FlushTimeout);

        Assert.Equal(["Свежая локальная"], data.Data.quotes);
        Assert.Equal(["Свежая локальная"], (await RemoteAsync(storage))!.quotes);
    }

    [Fact]
    public async Task Load_LegacyLocalWithoutStamp_LosesToStampedS3()
    {
        var storage = await s3.NewBucketAsync();
        await File.WriteAllTextAsync(LocalFile, Json(null, "Без updated_at"), Ct);
        await storage.PutTextAsync(Key, Json(DateTimeOffset.UtcNow, "С updated_at"), "application/json", Ct);

        var data = await LoadAsync(storage);
        await data.FlushAsync(FlushTimeout);

        Assert.Equal(["С updated_at"], data.Data.quotes);
    }

    [Fact]
    public async Task Load_S3WithoutLocalFile_UsesS3()
    {
        var storage = await s3.NewBucketAsync();
        await storage.PutTextAsync(Key, Json(DateTimeOffset.UtcNow, "Только в S3"), "application/json", Ct);

        // Без QuotesFile база живёт только в S3
        var data = await DataService.LoadAsync(null, storage, Key, 0, NullLogger.Instance);
        await data.FlushAsync(FlushTimeout);

        Assert.Equal(["Только в S3"], data.Data.quotes);
    }

    [Fact]
    public async Task Load_S3Unreachable_WithoutLocal_RefusesToStart()
    {
        s3.Require();
        var dead = new S3Storage("http://127.0.0.1:1", "bucket", S3Fixture.Region, S3Fixture.AccessKey, S3Fixture.SecretKey);

        // Иначе первый же Save() затёр бы базу в S3 пустой
        await Assert.ThrowsAsync<InvalidOperationException>(() => LoadAsync(dead));
    }

    [Fact]
    public async Task Load_S3Unreachable_WithLocal_StartsFromLocal()
    {
        s3.Require();
        await File.WriteAllTextAsync(LocalFile, Json(DateTimeOffset.UtcNow, "Локальная"), Ct);
        var dead = new S3Storage("http://127.0.0.1:1", "bucket", S3Fixture.Region, S3Fixture.AccessKey, S3Fixture.SecretKey);

        var data = await LoadAsync(dead);

        Assert.Equal(["Локальная"], data.Data.quotes);
        await data.FlushAsync(TimeSpan.FromMilliseconds(500));
    }

    [Fact]
    public async Task Flush_UploadsLatestVersion()
    {
        var storage = await s3.NewBucketAsync();
        var data = await LoadAsync(storage);

        for (var i = 1; i <= 5; i++)
            data.TryAddQuote($"Цитата {i}", _ => false);
        await data.FlushAsync(FlushTimeout);

        var remote = await RemoteAsync(storage);
        Assert.Equal(5, remote!.quotes.Count);
        Assert.Equal(data.Data.updated_at, remote.updated_at);
        // Кириллица в S3 не экранируется, чтобы базу можно было читать в веб-панели
        Assert.Contains("Цитата 5", await storage.GetTextAsync(Key, Ct));
    }

    [Fact]
    public async Task Save_IsUploadedInBackgroundAfterDebounce()
    {
        var storage = await s3.NewBucketAsync();
        await storage.PutTextAsync(Key, Json(DateTimeOffset.UtcNow.AddMinutes(-1)), "application/json", Ct);
        var data = await LoadAsync(storage);

        data.TryAddQuote("Фоновая", _ => false);

        // Отправка выжидает ~5 с, чтобы пачка Save() ушла одним PUT
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while ((await RemoteAsync(storage))!.quotes.Count == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "База не дошла до S3 за 20 секунд");
            await Task.Delay(500, Ct);
        }

        Assert.Equal(["Фоновая"], (await RemoteAsync(storage))!.quotes);
        await data.FlushAsync(FlushTimeout);
    }

    [Fact]
    public async Task Backup_CreatesDailyCopy()
    {
        var storage = await s3.NewBucketAsync();
        var data = await LoadAsync(storage, backupDays: 30);

        data.TryAddQuote("В бэкап", _ => false);
        await data.FlushAsync(FlushTimeout);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backupKey = $"backups/quotes-{today:yyyy-MM-dd}.json";
        Assert.Equal([backupKey], await storage.ListKeysAsync("backups/", Ct));
        Assert.Equal(["В бэкап"], (await RemoteAsync(storage, backupKey))!.quotes);
    }

    [Fact]
    public async Task Backup_PrunesToConfiguredDays()
    {
        var storage = await s3.NewBucketAsync();
        foreach (var day in new[] { "2020-01-01", "2020-01-02", "2020-01-03" })
            await storage.PutTextAsync($"backups/quotes-{day}.json", "{}", "application/json", Ct);
        // Чужие ключи с другим префиксом не трогаются
        await storage.PutTextAsync("backups/other-2020-01-01.json", "{}", "application/json", Ct);

        var data = await LoadAsync(storage, backupDays: 2);
        data.TryAddQuote("Цитата", _ => false);
        await data.FlushAsync(FlushTimeout);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        Assert.Equal(
            ["backups/other-2020-01-01.json", "backups/quotes-2020-01-03.json", $"backups/quotes-{today:yyyy-MM-dd}.json"],
            (await storage.ListKeysAsync("backups/", Ct)).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Backup_Disabled_WhenDaysIsZero()
    {
        var storage = await s3.NewBucketAsync();
        var data = await LoadAsync(storage, backupDays: 0);

        data.TryAddQuote("Цитата", _ => false);
        await data.FlushAsync(FlushTimeout);

        Assert.Empty(await storage.ListKeysAsync("backups/", Ct));
    }

    [Fact]
    public async Task RestartCycle_KeepsData()
    {
        // Остановка с отправкой в S3, затем старт на «новой машине» без локальной копии
        var storage = await s3.NewBucketAsync();
        var first = await LoadAsync(storage);
        first.TryAddQuote("Переживёт перезапуск", _ => false);
        new QuoteService(first).Vote(new QuoteService(first).HashOf("Переживёт перезапуск"), 1, like: true);
        await first.FlushAsync(FlushTimeout);

        using var otherDir = new TempDir();
        var second = await LoadAsync(storage, file: otherDir.File("quotes.json"));
        await second.FlushAsync(FlushTimeout);

        var quotes = new QuoteService(second);
        Assert.Equal(["Переживёт перезапуск"], second.Data.quotes);
        Assert.Equal((1, 0), quotes.GetCounts(quotes.HashOf("Переживёт перезапуск")));
    }
}
