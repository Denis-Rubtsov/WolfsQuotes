using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

class Program
{
    static void Main()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", true)
            .AddEnvironmentVariables()
            .Build();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(config.GetSection("Logging"));
            builder.AddConsole();
        });

        var quotesFile = config["QuotesFile"];
        var token = config["TelegramBot:Token"];
        var adminIds = (config["ADMIN_IDS"] ?? "0")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(id => long.Parse(id.Trim()))
            .ToList();
        var voice = config["BASIC_URL"] ?? "";
        var aiApiKey = config["AiApikey"];

        var bot = new TelegramBotClient(token);

        var data = new DataService(quotesFile);
        var quotes = new QuoteService(data);
        var ai = new AiQuoteService(aiApiKey, data);
        var inline = new InlineHandler(data, ai, adminIds, voice, loggerFactory.CreateLogger<InlineHandler>());

        var service = new BotService(bot, inline, data, quotes, ai, adminIds, voice, loggerFactory.CreateLogger<BotService>());

        service.Start();

        loggerFactory.CreateLogger<Program>().LogInformation("Бот запущен");
        Thread.Sleep(Timeout.Infinite);
    }
}
