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
        var promptFile = config["SystemPromptFile"];

        var bot = new TelegramBotClient(token);

        var data = new DataService(quotesFile);
        var quotes = new QuoteService(data);
        var ai = new AiQuoteService(aiApiKey, promptFile, data, loggerFactory.CreateLogger<AiQuoteService>());

        // Лимит ИИ-генераций для обычных пользователей; на админов не действует.
        var rateLimiter = new RateLimiter(5, TimeSpan.FromHours(1));

        // Цена одной генерации сверх лимита в звёздах Telegram; 0 отключает продажу.
        var starsPrice = int.TryParse(config["StarsPrice"], out var price) ? price : 10;

        var inline = new InlineHandler(data, ai, adminIds, voice, rateLimiter, loggerFactory.CreateLogger<InlineHandler>());

        var service = new BotService(bot, inline, data, quotes, ai, adminIds, rateLimiter, starsPrice, loggerFactory.CreateLogger<BotService>());

        service.Start();

        loggerFactory.CreateLogger<Program>().LogInformation("Бот запущен");
        Thread.Sleep(Timeout.Infinite);
    }
}
