using Microsoft.Extensions.Logging.Abstractions;

// Временная папка под файл базы; удаляется после теста
sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("vlk-tests-").FullName;

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); }
        catch (IOException) { }
    }
}

static class Db
{
    // База только с локальным файлом, без S3
    public static Task<DataService> LocalAsync(string file) =>
        DataService.LoadAsync(file, null, "quotes.json", 0, NullLogger.Instance);

    public static async Task<DataService> WithQuotesAsync(string file, params string[] quotes)
    {
        var data = await LocalAsync(file);
        foreach (var quote in quotes)
            Assert.True(data.TryAddQuote(quote, _ => false));
        return data;
    }
}
