using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InlineQueryResults;
using Telegram.Bot.Types.ReplyMarkups;
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
            Data = JsonSerializer.Deserialize<BotData>(File.ReadAllText(file))!;
        else
            Data = new BotData();
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
        var input = query.Query.Trim();
        var quoteCount = _data.Data.quotes.Count;

        string quote;
        string title = "Вспомнить мудрость";
        int number;

        if (int.TryParse(input, out int index))
        {
            index -= 1;

            if (index < 0 || index >= quoteCount)
            {
                await bot.AnswerInlineQueryAsync(
                    query.Id,
                    Array.Empty<InlineQueryResult>(),
                    cacheTime: 0,
                    isPersonal: true,
                    null,
                    switchPmText: $"Введите целое число от 1 до {_data.Data.quotes.Count}",
                    "start"
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
                new InputTextMessageContent(quote))
            {
                Description = quote[..Math.Min(80, quote.Length)]
            },
            new InlineQueryResultVoice(
                Guid.NewGuid().ToString(),
                voice,
                title + " голосом Волка")
        };

        await bot.AnswerInlineQueryAsync(
            query.Id,
            results,
            cacheTime: 0,
            isPersonal: true,
            null,
            switchPmText: $"Для получения конкретной цитаты введите целое число от 1 до {_data.Data.quotes.Count}",
            "start"
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

    private readonly Dictionary<long, Dictionary<string, string>> _userState = new();

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
            await HandleMessage(update);

        if (update.CallbackQuery != null)
            await HandleCallback(update);
    }

    private async Task HandleMessage(Update update)
    {
        var text = update.Message!.Text!;
        var user = update.Message.From!;
        var chatId = update.Message.Chat.Id;

        if (text.StartsWith("/start"))
        {
            await _bot.SendTextMessageAsync(chatId,
                "Добро пожаловать в бот \"Вълчьи цитаты\".\n\n/suggest — предложить цитату\n/list — список цитат");
            return;
        }

        if (text.StartsWith("/help"))
        {
            await _bot.SendTextMessageAsync(chatId,
                "/suggest\n/list\n/addquote\n/listsuggest\n/approve\n/reject");
            return;
        }

        if (text.StartsWith("/list") && !text.StartsWith("/listsuggest"))
        {
            var list = string.Join("\n",
                _data.Data.quotes.Select((q, i) => $"{i + 1}. {q}"));
            await _bot.SendTextMessageAsync(chatId, list);
            return;
        }

        if (text.StartsWith("/suggest"))
        {
            SetMode(user.Id, "suggest");
            await _bot.SendTextMessageAsync(chatId, "✍️ Введите цитату для предложения.");
            return;
        }

        if (text.StartsWith("/addquote") && user.Id == _adminId)
        {
            SetMode(user.Id, "add");
            await _bot.SendTextMessageAsync(chatId, "Введите цитату для добавления.");
            return;
        }

        if (text.StartsWith("/listsuggest") && user.Id == _adminId)
        {
            if (!_data.Data.suggestions.Any())
            {
                await _bot.SendTextMessageAsync(chatId, "Нет предложенных цитат");
                return;
            }

            var textOut = string.Join("\n",
                _data.Data.suggestions.Select((s, i) =>
                    $"{i + 1}. {s.quote} (от {s.user_id})"));

            await _bot.SendTextMessageAsync(chatId, textOut);
            return;
        }

        if (!_userState.ContainsKey(user.Id))
            return;

        _userState[user.Id]["pending_quote"] = text;

        var keyboard = new InlineKeyboardMarkup(
            new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("✅ Подтвердить","confirm"),
                    InlineKeyboardButton.WithCallbackData("❌ Отменить","cancel")
                }
            });

        await _bot.SendTextMessageAsync(
            chatId,
            $"Вот что вы ввели:\n\n{text}\n\nПодтвердить?",
            replyMarkup: keyboard);
    }

    private async Task HandleCallback(Update update)
    {
        var query = update.CallbackQuery!;
        var user = query.From;

        if (!_userState.ContainsKey(user.Id))
        {
            await _bot.AnswerCallbackQueryAsync(query.Id);
            return;
        }

        var state = _userState[user.Id];

        if (!state.ContainsKey("pending_quote"))
        {
            await _bot.AnswerCallbackQueryAsync(query.Id);
            return;
        }

        var quote = state["pending_quote"];
        var mode = state["mode"];

        if (query.Data == "confirm")
        {
            if (mode == "suggest")
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
                    "Цитата отправлена на рассмотрение.");
            }

            if (mode == "add" && user.Id == _adminId)
            {
                if (!_quotes.Exists(quote))
                {
                    _data.Data.quotes.Add(quote);
                    _data.Save();
                    await _bot.EditMessageTextAsync(
                        query.Message!.Chat.Id,
                        query.Message.MessageId,
                        "🔥 Цитата добавлена.");
                }
                else
                {
                    await _bot.EditMessageTextAsync(
                        query.Message!.Chat.Id,
                        query.Message.MessageId,
                        "⚠️ Такая цитата уже существует.");
                }
            }
        }

        if (query.Data == "cancel")
        {
            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Действие отменено.");
        }

        _userState.Remove(user.Id);
    }

    private void SetMode(long userId, string mode)
    {
        _userState[userId] = new Dictionary<string, string>
        {
            ["mode"] = mode
        };
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
        Thread.Sleep(Timeout.Infinite);
    }
}
