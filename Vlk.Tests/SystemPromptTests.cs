using Microsoft.Extensions.Logging.Abstractions;

// Выбор системного промпта из файла (SystemPromptFile); сам OpenAI не вызывается
public class SystemPromptTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private async Task<AiQuoteService> ServiceAsync(string? promptFile) =>
        new("test-key", promptFile, null, "systemprompt.txt", await Db.LocalAsync(_dir.File("quotes.json")),
            NullLogger<AiQuoteService>.Instance);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task NoPromptFile_UsesDefault(string? promptFile)
    {
        var ai = await ServiceAsync(promptFile);

        Assert.Equal(AiQuoteService.DefaultSystemPrompt, await ai.GetSystemPromptAsync());
    }

    [Fact]
    public async Task MissingFile_UsesDefault()
    {
        var ai = await ServiceAsync(_dir.File("нет.txt"));

        Assert.Equal(AiQuoteService.DefaultSystemPrompt, await ai.GetSystemPromptAsync());
    }

    [Fact]
    public async Task EmptyFile_UsesDefault()
    {
        var file = _dir.File("prompt.txt");
        await File.WriteAllTextAsync(file, " \n\t ", TestContext.Current.CancellationToken);
        var ai = await ServiceAsync(file);

        Assert.Equal(AiQuoteService.DefaultSystemPrompt, await ai.GetSystemPromptAsync());
    }

    [Fact]
    public async Task File_IsTrimmedAndRereadOnEveryCall()
    {
        var file = _dir.File("prompt.txt");
        var ai = await ServiceAsync(file);

        await File.WriteAllTextAsync(file, "  Первый промпт\n", TestContext.Current.CancellationToken);
        Assert.Equal("Первый промпт", await ai.GetSystemPromptAsync());

        // Правки промпта применяются без перезапуска
        await File.WriteAllTextAsync(file, "Второй промпт", TestContext.Current.CancellationToken);
        Assert.Equal("Второй промпт", await ai.GetSystemPromptAsync());
    }
}
