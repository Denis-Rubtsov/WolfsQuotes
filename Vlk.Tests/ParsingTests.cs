public class S3UrlTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("s3.ru-3.storage.selcloud.ru", "https://s3.ru-3.storage.selcloud.ru")]
    [InlineData("  s3.ru1.storage.beget.cloud  ", "https://s3.ru1.storage.beget.cloud")]
    [InlineData("https://s3.ru1.storage.beget.cloud", "https://s3.ru1.storage.beget.cloud")]
    [InlineData("http://localhost:9000", "http://localhost:9000")]
    public void WithScheme(string? input, string? expected) =>
        Assert.Equal(expected, S3Storage.WithScheme(input));

    [Theory]
    // Бакет в имени хоста (virtual-hosted style)
    [InlineData("https://voice.s3.ru1.storage.beget.cloud/", "https://s3.ru1.storage.beget.cloud")]
    [InlineData("https://my-bucket.s3.ru1.storage.beget.cloud/audio/", "https://s3.ru1.storage.beget.cloud")]
    // Бакет в пути (path style)
    [InlineData("https://s3.ru1.storage.beget.cloud/voice/", "https://s3.ru1.storage.beget.cloud")]
    [InlineData("https://s3.ru-3.storage.selcloud.ru/bucket/a/b/", "https://s3.ru-3.storage.selcloud.ru")]
    // Нестандартный порт сохраняется
    [InlineData("http://bucket.s3.local:9000/x/", "http://s3.local:9000")]
    // Не S3-хост — берётся как есть
    [InlineData("https://cdn.example.com/voice/", "https://cdn.example.com")]
    // Не URL
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("voice/", null)]
    public void ServiceUrlFrom(string? publicUrl, string? expected) =>
        Assert.Equal(expected, S3Storage.ServiceUrlFrom(publicUrl));
}

public class CommandParsingTests
{
    [Theory]
    [InlineData("/start", "/start", "")]
    [InlineData("/QUOTE", "/quote", "")]
    [InlineData("/editquote 3", "/editquote", "3")]
    [InlineData("/editquote   3  ", "/editquote", "3")]
    [InlineData("/approve@VlkBot 12", "/approve", "12")]
    [InlineData("/Help@VlkBot", "/help", "")]
    [InlineData("/setprice 15 звёзд", "/setprice", "15 звёзд")]
    [InlineData("просто текст", "просто", "текст")]
    public void ParseCommand(string text, string command, string args) =>
        Assert.Equal((command, args), BotService.ParseCommand(text));

    [Theory]
    [InlineData("5", true, 5)]
    [InlineData("  12 лишнее", true, 12)]
    [InlineData("-1", true, -1)]
    [InlineData("", false, 0)]
    [InlineData("   ", false, 0)]
    [InlineData("пять", false, 0)]
    [InlineData("99999999999", false, 0)]
    public void TryParseIndex(string args, bool ok, int index)
    {
        Assert.Equal(ok, BotService.TryParseIndex(args, out var parsed));
        Assert.Equal(index, parsed);
    }
}

public class UserStateTests
{
    [Fact]
    public void FreshState_IsNotExpired() =>
        Assert.False(new UserState { Mode = UserMode.Suggest }.IsExpired(TimeSpan.FromMinutes(30)));

    [Fact]
    public async Task State_ExpiresAfterTtl()
    {
        var state = new UserState { Mode = UserMode.Edit, EditIndex = 2 };

        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.True(state.IsExpired(TimeSpan.FromMilliseconds(10)));
    }

    [Fact]
    public void EditIndex_DefaultsToNone() =>
        Assert.Equal(-1, new UserState { Mode = UserMode.Add }.EditIndex);
}
