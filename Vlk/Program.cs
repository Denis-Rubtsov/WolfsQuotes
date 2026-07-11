using Microsoft.Extensions.Configuration;
using Telegram.Bot;

class Program
{
    static void Main()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", true)
            .AddEnvironmentVariables()
            .Build();

        var quotesFile = config["QuotesFile"];
        var token = config["TelegramBot:Token"];
        var admin = long.Parse(config["ADMIN_ID"] ?? "0");
        var voice = config["BASIC_URL"] ?? "";
        var aiApiKey = config["AiApikey"];

        var bot = new TelegramBotClient(token);

        var data = new DataService(quotesFile);
        var quotes = new QuoteService(data);
        var inline = new InlineHandler(quotes, data, voice);
        var ai = new AiQuoteService(aiApiKey, data);

        var service = new BotService(bot, inline, data, quotes, ai, admin, voice);

        service.Start();

        Console.WriteLine("Бот запущен");
        Thread.Sleep(Timeout.Infinite);
    }
}
