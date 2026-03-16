using System.Text.Json;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
using Microsoft.Extensions.Configuration;
using File = System.IO.File;

class BotData
{
    public List<string> quotes { get; set; } = new();
    public List<Suggestion> suggestions { get; set; } = new();
}

class Suggestion
{
    public long user_id { get; set; }
    public string name { get; set; } = "";
    public string quote { get; set; } = "";
}

class DataService
{
    private readonly string _file;
    public BotData Data { get; private set; }

    public DataService(string file)
    {
        _file = file;

        if (File.Exists(file))
        {
            Data = JsonSerializer.Deserialize<BotData>(File.ReadAllText(file))!;
        }
        else
        {
            Data = new BotData();
        }
    }

    public void Save()
    {
        File.WriteAllText(_file,
            JsonSerializer.Serialize(Data, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
    }
}

class QuoteService
{
    private readonly DataService _data;
    private readonly Random _random = new();

    public QuoteService(DataService data)
    {
        _data = data;
    }

    public string GetRandom()
    {
        return _data.Data.quotes[_random.Next(_data.Data.quotes.Count)];
    }

    public bool Exists(string quote)
    {
        var normalized = quote.Trim().ToLower();
        return _data.Data.quotes.Any(q => q.Trim().ToLower() == normalized);
    }
}

class InlineHandler
{
    private readonly QuoteService _quotes;
    private readonly DataService _data;
    private readonly string _voiceUrl;

    public InlineHandler(QuoteService quotes, DataService data, string voiceUrl)
    {
        _quotes = quotes;
        _data = data;
        _voiceUrl = voiceUrl;
    }

    public async Task Handle(ITelegramBotClient bot, InlineQuery query)
    {
        string quote;
        string title = "Вспомнить мудрость";
        int number;

        if (int.TryParse(query.Query, out int index))
        {
            index -= 1;

            if (index < 0 || index >= _data.Data.quotes.Count)
            {
                await bot.AnswerInlineQueryAsync(
                    query.Id,
                    Array.Empty<InlineQueryResult>(),
                    cacheTime: 0,
                    isPersonal: true
                );
                return;
            }

            quote = _data.Data.quotes[index];
            number = index + 1;
            title = $"Мудрость №{number}";
        }
        else
        {
            quote = _quotes.GetRandom();
            number = _data.Data.quotes.IndexOf(quote) + 1;
        }

        var voice = _voiceUrl + $"{number}.ogg";

        var results = new InlineQueryResult[]
        {
            new InlineQueryResultArticle(
                Guid.NewGuid().ToString(),
                title,
                new InputTextMessageContent(quote)
            )
            {
                Description = quote[..Math.Min(80, quote.Length)]
            },

            new InlineQueryResultVoice(
                Guid.NewGuid().ToString(), 
                voice,
                title + " голосом Волка"
            )
        };

        await bot.AnswerInlineQueryAsync(
            query.Id,
            results,
            cacheTime: 0,
            isPersonal: true
        );
    }
}

class BotService
{
    private readonly ITelegramBotClient _bot;
    private readonly InlineHandler _inline;
    private readonly DataService _data;
    private readonly QuoteService _quotes;
    private readonly long _adminId;

    private readonly Dictionary<long, string> _userMode = new();
    private readonly Dictionary<long, string> _pendingQuote = new();

    public BotService(
        ITelegramBotClient bot,
        InlineHandler inline,
        DataService data,
        QuoteService quotes,
        long adminId)
    {
        _bot = bot;
        _inline = inline;
        _data = data;
        _quotes = quotes;
        _adminId = adminId;
    }

    public void Start()
    {
        _bot.StartReceiving(Update, Error);
    }

    private async Task Update(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Type == UpdateType.InlineQuery)
        {
            await _inline.Handle(bot, update.InlineQuery!);
            return;
        }

        if (update.Message?.Text != null)
        {
            await HandleMessage(update);
        }

        if (update.CallbackQuery != null)
        {
            await HandleCallback(update);
        }
    }

    private async Task HandleMessage(Update update)
    {
        var text = update.Message!.Text!;
        var user = update.Message.From!;

        if (text.StartsWith("/suggest"))
        {
            _userMode[user.Id] = "suggest";
            await _bot.SendTextMessageAsync(update.Message.Chat.Id, "✍️ Введите цитату.");
            return;
        }

        if (text.StartsWith("/list"))
        {
            var all = string.Join("\n",
                _data.Data.quotes.Select((q, i) => $"{i + 1}. {q}"));

            await _bot.SendTextMessageAsync(update.Message.Chat.Id, all);
            return;
        }

        if (_userMode.ContainsKey(user.Id))
        {
            _pendingQuote[user.Id] = text;

            var keyboard = new InlineKeyboardMarkup(
                new[]
                {
                    new[]
                    {
                        InlineKeyboardButton.WithCallbackData("✅ Подтвердить","confirm"),
                        InlineKeyboardButton.WithCallbackData("❌ Отмена","cancel")
                    }
                });

            await _bot.SendTextMessageAsync(
                update.Message.Chat.Id,
                $"Вы ввели:\n\n{text}",
                replyMarkup: keyboard
            );
        }
    }

    private async Task HandleCallback(Update update)
    {
        var query = update.CallbackQuery!;
        var user = query.From;

        if (!_pendingQuote.ContainsKey(user.Id))
        {
            await _bot.AnswerCallbackQueryAsync(query.Id, "Данные устарели");
            return;
        }

        var quote = _pendingQuote[user.Id];

        if (query.Data == "confirm")
        {
            if (_userMode[user.Id] == "suggest")
            {
                _data.Data.suggestions.Add(new Suggestion
                {
                    user_id = user.Id,
                    name = user.Username ?? user.FirstName,
                    quote = quote
                });

                _data.Save();

                await _bot.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "Цитата отправлена на рассмотрение");
            }
        }

        if (query.Data == "cancel")
        {
            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "Отменено");
        }

        _pendingQuote.Remove(user.Id);
        _userMode.Remove(user.Id);
    }

    private Task Error(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Console.WriteLine(ex);
        return Task.CompletedTask;
    }
}

class Program
{
    static void Main()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", true)
            .AddEnvironmentVariables()
            .Build();

        var token = config["TelegramBot:Token"];
        var admin = long.Parse(config["ADMIN_ID"] ?? "0");
        var voice = config["BASIC_URL"] ?? "";

        var bot = new TelegramBotClient(token);

        var data = new DataService("/data/quotes.json");
        var quotes = new QuoteService(data);
        var inline = new InlineHandler(quotes, data, voice);

        var service = new BotService(bot, inline, data, quotes, admin);

        service.Start();

        Console.WriteLine("Бот запущен");
        Console.ReadLine();
    }
}
