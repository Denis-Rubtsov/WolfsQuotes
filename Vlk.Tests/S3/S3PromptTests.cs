using Microsoft.Extensions.Logging.Abstractions;

// Системный промпт из S3: перечитывается на каждую генерацию по ETag
[Collection("S3")]
public class S3PromptTests(S3Fixture s3) : IDisposable
{
    private const string PromptKey = "systemprompt.txt";

    private readonly TempDir _dir = new();
    private CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _dir.Dispose();

    private async Task<AiQuoteService> ServiceAsync(S3Storage storage) =>
        new("test-key", null, storage, PromptKey, await Db.LocalAsync(_dir.File("quotes.json")),
            NullLogger<AiQuoteService>.Instance);

    [Fact]
    public async Task MissingPrompt_UsesDefault()
    {
        var storage = await s3.NewBucketAsync();
        var ai = await ServiceAsync(storage);

        Assert.Equal(AiQuoteService.DefaultSystemPrompt, await ai.GetSystemPromptAsync());
    }

    [Fact]
    public async Task Prompt_IsTrimmedAndFollowsChanges()
    {
        var storage = await s3.NewBucketAsync();
        var ai = await ServiceAsync(storage);

        await storage.PutTextAsync(PromptKey, "  Промпт из S3\n", "text/plain", Ct);
        Assert.Equal("Промпт из S3", await ai.GetSystemPromptAsync());
        // Повторный запрос с тем же ETag отдаёт закешированный текст
        Assert.Equal("Промпт из S3", await ai.GetSystemPromptAsync());

        await storage.PutTextAsync(PromptKey, "Новый промпт", "text/plain", Ct);
        Assert.Equal("Новый промпт", await ai.GetSystemPromptAsync());
    }

    [Fact]
    public async Task BlankPrompt_UsesDefault()
    {
        var storage = await s3.NewBucketAsync();
        var ai = await ServiceAsync(storage);

        await storage.PutTextAsync(PromptKey, " \n ", "text/plain", Ct);

        Assert.Equal(AiQuoteService.DefaultSystemPrompt, await ai.GetSystemPromptAsync());
    }

    [Fact]
    public async Task DeletedPrompt_FallsBackToDefault()
    {
        var storage = await s3.NewBucketAsync();
        var ai = await ServiceAsync(storage);
        await storage.PutTextAsync(PromptKey, "Временный", "text/plain", Ct);
        Assert.Equal("Временный", await ai.GetSystemPromptAsync());

        await storage.DeleteAsync(PromptKey, Ct);

        Assert.Equal(AiQuoteService.DefaultSystemPrompt, await ai.GetSystemPromptAsync());
    }
}
