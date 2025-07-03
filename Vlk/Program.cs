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
        "Вълкъ - не тот, кто wolf, а тот, кто Canis lupus",
        "Если вълкъ и работает в цирке, то только начальником",
        "Если вълкъ голодный, лучше его покормить",
        "Если запутался, распутайся",
        "Чтобы не искать выход, посмотри внимательно на вход.",
        "Каждый может кинуть камень в вълка, но не каждый может кинуть вълка в камень",
        "Робота - не волк. Работа - это ворк, а волк - это ходить",
        "Не тот волк кто не волк а волк тот кто волк но не каждый волк настоящий волк а только настоящий волк волк",
        "Припапупапри",
        "Не стоит искать вълка там, где его нет, - его там нет",
        "Друг наполовину - это всегда наполовину друг",
        "Если дофига умный - умничай, но помни: без ума умничать не выйдет. Так что будь умным, чтобы умничать",
        "В этой жизни ты либо вълкъ, либо не вълкъ",
        "Что бессмысленно, то не имеет смысла"
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
                    inputMessageContent: new InputTextMessageContent($"{GetRandomQuote()}\n\nМудростью поделился Великий Вълкъ - @Vlk_quote_bot")
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