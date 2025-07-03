using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Microsoft.Extensions.Configuration;

static string GetRandomQuote()
{
    var responses = new[]
    {
        "Вълкъ слабее льва и тигра, но в цирке не выступает",
        "Вълкъ - не тот, кто волк, а тот, кто Canis lupus",
        "Если вълкъ и работает в цирке, то только начальником",
        "Если вълкъ голодный, лучше его покормить",
        "Если запутался, распутайся",
        "Чтобы не искать выход, посмотри внимательно на вход.",
        "Каждый может кинуть камень в вълка, но не каждый может кинуть вълка в камень",
        "Робота - не волк. Работа - это ворк, а волк - это ходить"
    };

    var random = new Random();
    return responses[random.Next(responses.Length)];
}

var config = new ConfigurationBuilder()
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables() // даёт приоритет переменным среды
    .Build();

var botToken = config["TelegramBot:Token"];

var botClient = new TelegramBotClient(botToken);

botClient.StartReceiving(
    async (bot, update, ct) =>
    {
        if (update.Type == UpdateType.InlineQuery)
        {
            var inlineQuery = update.InlineQuery!;
            var results = new[]
            {
                new InlineQueryResultArticle(
                    id: "1",
                    title: "Вспомнить мудрость",
                    inputMessageContent: new InputTextMessageContent(GetRandomQuote())
                )
                {
                    Description = "Вы вспоминаете мудрость Великого вълка"
                }
            };

            await bot.AnswerInlineQueryAsync(
                inlineQueryId: inlineQuery.Id,
                results: results,
                isPersonal: true,
                cacheTime: 0
            );
        }
    },
    (bot, exception, ct) =>
    {
        Console.WriteLine(exception);
        return Task.CompletedTask;
    });

Console.WriteLine("Бот запущен. Нажми Enter для выхода.");
Console.ReadLine();