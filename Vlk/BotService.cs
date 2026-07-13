using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.ReplyMarkups;

class BotService
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(30);

    private readonly ITelegramBotClient _bot;
    private readonly InlineHandler _inline;
    private readonly DataService _data;
    private readonly QuoteService _quotes;
    private readonly AiQuoteService _ai;
    private readonly HashSet<long> _adminIds;
    private readonly string _voiceUrl;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<BotService> _logger;

    private readonly object _userStateLock = new();
    private readonly Dictionary<long, UserState> _userState = new();

    public BotService(ITelegramBotClient bot, InlineHandler inline, DataService data, QuoteService quotes, AiQuoteService ai, IEnumerable<long> adminIds, string voiceUrl, RateLimiter rateLimiter, ILogger<BotService> logger)
    {
        _bot = bot;
        _inline = inline;
        _data = data;
        _quotes = quotes;
        _ai = ai;
        _adminIds = new HashSet<long>(adminIds);
        _voiceUrl = voiceUrl;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public void Start()
    {
        try
        {
            _bot.SetMyCommandsAsync(new[]
            {
                new BotCommand { Command = "quote", Description = "Случайная цитата" },
                new BotCommand { Command = "suggest", Description = "Предложить цитату" },
                new BotCommand { Command = "list", Description = "Список цитат" },
                new BotCommand { Command = "guide", Description = "Подробный гайд по боту" },
                new BotCommand { Command = "help", Description = "Показать помощь" },
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось установить меню команд по умолчанию");
        }

        var adminCommands = new[]
        {
            new BotCommand { Command = "quote", Description = "Случайная цитата" },
            new BotCommand { Command = "suggest", Description = "Предложить цитату" },
            new BotCommand { Command = "generate", Description = "Сгенерировать цитату через ИИ" },
            new BotCommand { Command = "addquote", Description = "Добавить цитату" },
            new BotCommand { Command = "editquote", Description = "Редактировать цитату" },
            new BotCommand { Command = "deletequote", Description = "Удалить цитату" },
            new BotCommand { Command = "listsuggest", Description = "Список предложений" },
            new BotCommand { Command = "approve", Description = "Принять предложение" },
            new BotCommand { Command = "reject", Description = "Отклонить предложение" },
            new BotCommand { Command = "publicgen", Description = "Вкл/выкл генерацию для всех" },
            new BotCommand { Command = "stats", Description = "Статистика" },
            new BotCommand { Command = "export", Description = "Экспорт базы цитат" },
            new BotCommand { Command = "list", Description = "Список цитат" },
            new BotCommand { Command = "guide", Description = "Подробный гайд по боту" },
            new BotCommand { Command = "help", Description = "Показать помощь" },
        };

        foreach (var adminId in _adminIds)
        {
            try
            {
                _bot.SetMyCommandsAsync(adminCommands, scope: new BotCommandScopeChat { ChatId = adminId })
                    .GetAwaiter().GetResult();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось установить меню команд для админа {AdminId}", adminId);
            }
        }

        _bot.StartReceiving(Update, Error);
    }

    private bool IsAdmin(long userId) => _adminIds.Contains(userId);

    private bool CanGenerate(long userId)
    {
        if (IsAdmin(userId))
            return true;

        lock (_data.Lock)
        {
            return _data.Data.allow_public_generation;
        }
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
        var user = update.Message.From;
        var chatId = update.Message.Chat.Id;

        if (user == null)
            return;

        if (!text.StartsWith('/'))
        {
            await HandlePendingInput(chatId, user.Id, text);
            return;
        }

        var (command, args) = ParseCommand(text);

        switch (command)
        {
            case "/start":
                await HandleStart(chatId);
                break;

            case "/help":
                await HandleHelp(chatId, user.Id);
                break;

            case "/guide":
                await HandleGuide(chatId, user.Id);
                break;

            case "/list":
                await HandleList(chatId);
                break;

            case "/quote":
                await HandleQuote(chatId);
                break;

            case "/suggest":
                await HandleSuggest(chatId, user.Id);
                break;

            case "/generate" when CanGenerate(user.Id):
                await HandleGenerate(chatId, user.Id);
                break;

            case "/publicgen" when IsAdmin(user.Id):
                await HandleTogglePublicGeneration(chatId);
                break;

            case "/stats" when IsAdmin(user.Id):
                await HandleStats(chatId);
                break;

            case "/export" when IsAdmin(user.Id):
                await HandleExport(chatId);
                break;

            case "/addquote" when IsAdmin(user.Id):
                await HandleAddQuote(chatId, user.Id);
                break;

            case "/editquote" when IsAdmin(user.Id):
                await HandleEditQuote(chatId, user.Id, args);
                break;

            case "/deletequote" when IsAdmin(user.Id):
                await HandleDeleteQuote(chatId, args);
                break;

            case "/listsuggest" when IsAdmin(user.Id):
                await HandleListSuggest(chatId);
                break;

            case "/reject" when IsAdmin(user.Id):
                await HandleReject(chatId, args);
                break;

            case "/approve" when IsAdmin(user.Id):
                await HandleApprove(chatId, args, user);
                break;

            default:
                await HandlePendingInput(chatId, user.Id, text);
                break;
        }
    }

    private static (string Command, string Args) ParseCommand(string text)
    {
        var parts = text.Split(' ', 2, StringSplitOptions.TrimEntries);
        var command = parts[0];

        var at = command.IndexOf('@');
        if (at >= 0)
            command = command[..at];

        return (command.ToLowerInvariant(), parts.Length > 1 ? parts[1] : "");
    }

    private static bool TryParseIndex(string args, out int index)
    {
        index = 0;
        var token = args.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return token != null && int.TryParse(token, out index);
    }

    private async Task HandleStart(long chatId)
    {
        await _bot.SendTextMessageAsync(chatId,
            "Добро пожаловать в бот \"Вълчьи цитаты\".\n\n/help - список команд\n/start - запуск бота\n/suggest — предложить цитату\n/list — список цитат");
    }

    private async Task HandleHelp(long chatId, long userId)
    {
        if (IsAdmin(userId))
        {
            await _bot.SendTextMessageAsync(chatId,
                "Общие команды:\n\n/help - список команд\n/start - запуск бота\n/quote - случайная цитата\n/suggest - предложить цитату\n/list - список цитат\nАдминские команды:\n\n/addquote - добавить цитату\n/editquote <номер> - редактировать цитату\n/deletequote <номер> - удалить цитату\n/generate - сгенерировать цитату через ИИ\n/publicgen - вкл/выкл генерацию для всех\n/listsuggest - список предложений\n/approve - принять предложение\n/reject - отклонить предложение\n/stats - статистика\n/export - экспорт базы цитат\n\nВ inline-режиме (@botname ai) можно сгенерировать цитату через ИИ прямо в любом чате.");
            return;
        }

        var generateLine = CanGenerate(userId)
            ? "\n/generate - сгенерировать цитату через ИИ (уйдёт на модерацию)"
            : "";

        await _bot.SendTextMessageAsync(chatId,
            $"Список команд:\n\n/help - список команд\n/guide - подробный гайд по боту\n/start - запуск бота\n/quote - случайная цитата\n/suggest - предложить цитату{generateLine}\n/list - список цитат\n");
    }

    private async Task HandleGuide(long chatId, long userId)
    {
        var text = new StringBuilder();

        text.AppendLine("📖 Подробный гайд по боту \"Вълчьи цитаты\"");
        text.AppendLine();
        text.AppendLine("🐺 Получить цитату");
        text.AppendLine("/quote — бот пришлёт случайную цитату из базы. Под ней есть кнопки 👍/👎: " +
                        "у каждого пользователя один голос на цитату, повторное нажатие снимает голос, " +
                        "нажатие на противоположную кнопку переносит его.");
        text.AppendLine();
        text.AppendLine("📜 Список цитат");
        text.AppendLine("/list — вся база пронумерованным списком. Номера из него используются в inline-режиме и в админских командах.");
        text.AppendLine();
        text.AppendLine("✍️ Предложить свою цитату");
        text.AppendLine("1. Отправьте /suggest.");
        text.AppendLine("2. Следующим сообщением пришлите текст цитаты.");
        text.AppendLine("3. Подтвердите отправку кнопкой — предложение уйдёт на модерацию админам.");
        text.AppendLine("После одобрения цитата появится в базе. На ввод даётся 30 минут, потом заявка сбрасывается.");
        text.AppendLine();
        text.AppendLine("🤖 Генерация через ИИ");

        if (IsAdmin(userId))
        {
            text.AppendLine("/generate — бот сгенерирует цитату в стиле существующих. Перед добавлением её можно перегенерировать кнопкой. " +
                            "Как админ вы добавляете результат сразу в базу, без модерации и без лимитов.");
        }
        else if (CanGenerate(userId))
        {
            text.AppendLine("/generate — бот сгенерирует цитату в стиле существующих. Перед отправкой её можно перегенерировать кнопкой; " +
                            "результат уходит в очередь предложений на модерацию. Лимит — 5 генераций в час.");
        }
        else
        {
            text.AppendLine("Сейчас генерация доступна только админам. Когда админы откроют её командой /publicgen, " +
                            "появится команда /generate: результат уходит на модерацию, лимит — 5 генераций в час.");
        }

        text.AppendLine();
        text.AppendLine("⚡ Inline-режим (в любом чате)");
        text.AppendLine("Напишите @имя_бота в поле сообщения:");
        text.AppendLine("• @имя_бота — случайная цитата;");
        text.AppendLine("• @имя_бота 5 — цитата №5 из /list.");

        if (IsAdmin(userId))
        {
            text.AppendLine("• @имя_бота ai — сгенерировать цитату через ИИ прямо в чате.");
            text.AppendLine();
            text.AppendLine("👑 Админские возможности");
            text.AppendLine();
            text.AppendLine("Управление базой:");
            text.AppendLine("/addquote — добавить цитату напрямую, минуя модерацию (текст — следующим сообщением);");
            text.AppendLine("/editquote <номер> — заменить текст цитаты (номер из /list). Внимание: при изменении текста голоса цитаты обнуляются;");
            text.AppendLine("/deletequote <номер> — удалить цитату (с подтверждением).");
            text.AppendLine();
            text.AppendLine("Модерация предложений:");
            text.AppendLine("/listsuggest — очередь предложений от пользователей;");
            text.AppendLine("/approve <номер> — принять предложение (цитата попадёт в базу);");
            text.AppendLine("/reject <номер> — отклонить предложение.");
            text.AppendLine();
            text.AppendLine("Прочее:");
            text.AppendLine("/publicgen — открыть/закрыть генерацию через ИИ для обычных пользователей (настройка переживает перезапуск);");
            text.AppendLine("/stats — размер базы, очередь, голоса и топ цитат по лайкам;");
            text.AppendLine("/export — бот пришлёт JSON-файл базы (бэкап в один клик).");
            text.AppendLine();
            text.AppendLine("При добавлении любой цитаты (вручную, через ИИ или одобрением) остальные админы получают уведомление.");
        }

        text.AppendLine();
        text.AppendLine("Краткий список команд — /help.");

        await _bot.SendTextMessageAsync(chatId, text.ToString());
    }

    private async Task HandleList(long chatId)
    {
        List<string> quotesSnapshot;
        lock (_data.Lock)
        {
            quotesSnapshot = new List<string>(_data.Data.quotes);
        }

        if (quotesSnapshot.Count == 0)
        {
            await _bot.SendTextMessageAsync(chatId, "Цитат пока нет.");
            return;
        }

        var list = string.Join("\n",
            quotesSnapshot.Select((q, i) => $"{i + 1}. {q}"));
        await _bot.SendTextMessageAsync(chatId, list);
    }

    private async Task HandleQuote(long chatId)
    {
        var quote = _quotes.GetRandomQuote();

        if (quote == null)
        {
            await _bot.SendTextMessageAsync(chatId, "Цитат пока нет.");
            return;
        }

        var hash = _quotes.HashOf(quote);
        var (likes, dislikes) = _quotes.GetCounts(hash);

        await _bot.SendTextMessageAsync(chatId, quote,
            replyMarkup: RatingKeyboard(hash, likes, dislikes));
    }

    private async Task HandleStats(long chatId)
    {
        int quoteCount, suggestionCount;
        bool publicGen;
        List<string> quotesSnapshot;
        Dictionary<string, QuoteRating> ratingsSnapshot;

        lock (_data.Lock)
        {
            quoteCount = _data.Data.quotes.Count;
            suggestionCount = _data.Data.suggestions.Count;
            publicGen = _data.Data.allow_public_generation;
            quotesSnapshot = new List<string>(_data.Data.quotes);
            ratingsSnapshot = new Dictionary<string, QuoteRating>(_data.Data.ratings);
        }

        var totalLikes = ratingsSnapshot.Values.Sum(r => r.likes.Count);
        var totalDislikes = ratingsSnapshot.Values.Sum(r => r.dislikes.Count);

        var text = "📊 Статистика:\n\n" +
                   $"Цитат в базе: {quoteCount}\n" +
                   $"Предложений в очереди: {suggestionCount}\n" +
                   $"Генерация для всех: {(publicGen ? "включена" : "выключена")}\n" +
                   $"Голоса: 👍 {totalLikes} / 👎 {totalDislikes}";

        var top = quotesSnapshot
            .Select(q => (Quote: q, Likes: ratingsSnapshot.TryGetValue(_quotes.HashOf(q), out var r) ? r.likes.Count : 0))
            .Where(x => x.Likes > 0)
            .OrderByDescending(x => x.Likes)
            .Take(3)
            .ToList();

        if (top.Count > 0)
        {
            text += "\n\nТоп по лайкам:\n" + string.Join("\n",
                top.Select((x, i) => $"{i + 1}. {x.Quote} — 👍 {x.Likes}"));
        }

        await _bot.SendTextMessageAsync(chatId, text);
    }

    private async Task HandleExport(long chatId)
    {
        string json;
        lock (_data.Lock)
        {
            json = JsonSerializer.Serialize(_data.Data, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }

        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));

        await _bot.SendDocumentAsync(chatId,
            new InputOnlineFile(stream, "quotes.json"),
            caption: $"📦 Экспорт базы от {DateTime.UtcNow:yyyy-MM-dd HH:mm} UTC");
    }

    private async Task HandleSuggest(long chatId, long userId)
    {
        SetState(userId, new UserState { Mode = UserMode.Suggest });
        await _bot.SendTextMessageAsync(chatId, "✍️ Введите цитату для предложения.");
    }

    private async Task HandleGenerate(long chatId, long userId)
    {
        if (!IsAdmin(userId) && !_rateLimiter.TryAcquire(userId))
        {
            await _bot.SendTextMessageAsync(chatId, "⏳ Лимит генераций исчерпан. Попробуйте позже.");
            return;
        }

        var placeholder = await _bot.SendTextMessageAsync(chatId, "🐺 Генерирую цитату...");

        string generated;
        try
        {
            generated = await _ai.GenerateQuoteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка генерации цитаты через ИИ");
            await _bot.EditMessageTextAsync(chatId, placeholder.MessageId,
                "⚠️ Не удалось сгенерировать цитату. Попробуйте позже.");
            return;
        }

        // Админ добавляет сразу в базу, обычный пользователь — через очередь предложений.
        var forAdd = IsAdmin(userId);

        SetState(userId, new UserState
        {
            Mode = forAdd ? UserMode.Add : UserMode.Suggest,
            PendingQuote = generated
        });

        await _bot.EditMessageTextAsync(
            chatId,
            placeholder.MessageId,
            GeneratedQuoteText(generated, forAdd),
            replyMarkup: GeneratedQuoteKeyboard(forAdd));
    }

    private async Task HandleTogglePublicGeneration(long chatId)
    {
        bool enabled;
        lock (_data.Lock)
        {
            _data.Data.allow_public_generation = !_data.Data.allow_public_generation;
            enabled = _data.Data.allow_public_generation;
            _data.Save();
        }

        await _bot.SendTextMessageAsync(chatId,
            enabled
                ? "🌍 Генерация цитат через ИИ теперь доступна всем. Цитаты обычных пользователей идут в очередь предложений."
                : "🔒 Генерация цитат через ИИ снова доступна только админам.");
    }

    private async Task HandleAddQuote(long chatId, long userId)
    {
        SetState(userId, new UserState { Mode = UserMode.Add });
        await _bot.SendTextMessageAsync(chatId, "Введите цитату для добавления.");
    }

    private async Task HandleEditQuote(long chatId, long userId, string args)
    {
        if (!TryParseIndex(args, out int index))
        {
            await _bot.SendTextMessageAsync(chatId, "Использование: /editquote <номер>");
            return;
        }

        index -= 1;

        string? current = null;
        lock (_data.Lock)
        {
            if (index >= 0 && index < _data.Data.quotes.Count)
                current = _data.Data.quotes[index];
        }

        if (current == null)
        {
            await _bot.SendTextMessageAsync(chatId, "Неверный номер цитаты");
            return;
        }

        SetState(userId, new UserState { Mode = UserMode.Edit, EditIndex = index });

        await _bot.SendTextMessageAsync(chatId,
            $"✏️ Текущая цитата №{index + 1}:\n\n{current}\n\nВведите новый текст.");
    }

    private async Task HandleDeleteQuote(long chatId, string args)
    {
        if (!TryParseIndex(args, out int index))
        {
            await _bot.SendTextMessageAsync(chatId, "Использование: /deletequote <номер>");
            return;
        }

        index -= 1;

        string? current = null;
        lock (_data.Lock)
        {
            if (index >= 0 && index < _data.Data.quotes.Count)
                current = _data.Data.quotes[index];
        }

        if (current == null)
        {
            await _bot.SendTextMessageAsync(chatId, "Неверный номер цитаты");
            return;
        }

        var deleteKeyboard = new InlineKeyboardMarkup(
            new[]
            {
                new[]
                {
                    InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"delquote_{index}"),
                    InlineKeyboardButton.WithCallbackData("❌ Отменить", "delquote_cancel")
                }
            });

        await _bot.SendTextMessageAsync(chatId,
            $"Удалить цитату №{index + 1}?\n\n{current}",
            replyMarkup: deleteKeyboard);
    }

    private async Task HandleListSuggest(long chatId)
    {
        List<Suggestion> suggestionsSnapshot;
        lock (_data.Lock)
        {
            suggestionsSnapshot = new List<Suggestion>(_data.Data.suggestions);
        }

        if (!suggestionsSnapshot.Any())
        {
            await _bot.SendTextMessageAsync(chatId, "Нет предложенных цитат");
            return;
        }

        var textOut = string.Join("\n",
            suggestionsSnapshot.Select((s, i) =>
                $"{i + 1}. {s.quote} (от {s.name})"));

        await _bot.SendTextMessageAsync(chatId, textOut);
    }

    private async Task HandleReject(long chatId, string args)
    {
        if (!TryParseIndex(args, out int index))
        {
            await _bot.SendTextMessageAsync(chatId, "Использование: /reject <номер>");
            return;
        }

        index -= 1;

        Suggestion? removed = null;
        lock (_data.Lock)
        {
            if (index >= 0 && index < _data.Data.suggestions.Count)
            {
                removed = _data.Data.suggestions[index];
                _data.Data.suggestions.RemoveAt(index);
                _data.Save();
            }
        }

        if (removed == null)
        {
            await _bot.SendTextMessageAsync(chatId, "Неверный номер предложения");
            return;
        }

        await _bot.SendTextMessageAsync(chatId,
            $"❌ Отклонено: {removed.quote}");
    }

    private async Task HandleApprove(long chatId, string args, User user)
    {
        if (!TryParseIndex(args, out int index))
        {
            await _bot.SendTextMessageAsync(chatId, "Использование: /approve <номер>");
            return;
        }

        index -= 1;

        Suggestion? suggestion = null;
        lock (_data.Lock)
        {
            if (index >= 0 && index < _data.Data.suggestions.Count)
            {
                suggestion = _data.Data.suggestions[index];

                if (!_quotes.Exists(suggestion.quote))
                    _data.Data.quotes.Add(suggestion.quote);

                _data.Data.suggestions.RemoveAt(index);
                _data.Save();
            }
        }

        if (suggestion == null)
        {
            await _bot.SendTextMessageAsync(chatId, "Неверный номер предложения");
            return;
        }

        await _bot.SendTextMessageAsync(chatId,
            $"✅ Добавлено: {suggestion.quote}");

        await NotifyAdminsQuoteAdded(suggestion.quote, user.Id, user.Username ?? user.FirstName);
    }

    private async Task HandlePendingInput(long chatId, long userId, string text)
    {
        if (!TrySetPendingQuote(userId, text))
            return;

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

        if (query.Data != null && (query.Data.StartsWith("rate_l_") || query.Data.StartsWith("rate_d_")))
        {
            var like = query.Data.StartsWith("rate_l_");
            var hash = query.Data.Substring("rate_l_".Length);

            var counts = _quotes.Vote(hash, user.Id, like);

            if (counts == null)
            {
                await _bot.AnswerCallbackQueryAsync(query.Id, "⚠️ Этой цитаты больше нет.");
                return;
            }

            await _bot.EditMessageReplyMarkupAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                RatingKeyboard(hash, counts.Value.Likes, counts.Value.Dislikes));

            await _bot.AnswerCallbackQueryAsync(query.Id, "Голос учтён");
            return;
        }

        if (query.Data != null && query.Data.StartsWith("approve_") && IsAdmin(user.Id))
        {
            var id = query.Data.Substring("approve_".Length);
            Suggestion? suggestion = null;

            lock (_data.Lock)
            {
                var index = _data.Data.suggestions.FindIndex(s => s.id == id);

                if (index >= 0)
                {
                    suggestion = _data.Data.suggestions[index];

                    if (!_quotes.Exists(suggestion.quote))
                        _data.Data.quotes.Add(suggestion.quote);

                    _data.Data.suggestions.RemoveAt(index);
                    _data.Save();
                }
            }

            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                suggestion != null ? "✅ Цитата одобрена и добавлена." : "⚠️ Это предложение уже обработано."
            );

            if (suggestion != null)
                await NotifyAdminsQuoteAdded(suggestion.quote, user.Id, user.Username ?? user.FirstName);

            return;
        }

        if (query.Data != null && query.Data.StartsWith("reject_") && IsAdmin(user.Id))
        {
            var id = query.Data.Substring("reject_".Length);
            bool removed;

            lock (_data.Lock)
            {
                var index = _data.Data.suggestions.FindIndex(s => s.id == id);
                removed = index >= 0;

                if (removed)
                {
                    _data.Data.suggestions.RemoveAt(index);
                    _data.Save();
                }
            }

            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                removed ? "❌ Цитата отклонена." : "⚠️ Это предложение уже обработано."
            );

            return;
        }

        if (query.Data != null && query.Data.StartsWith("delquote_") && IsAdmin(user.Id))
        {
            var rest = query.Data.Substring("delquote_".Length);

            if (rest == "cancel")
            {
                await _bot.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    "❌ Удаление отменено.");
                return;
            }

            string? removedQuote = null;
            if (int.TryParse(rest, out int deleteIndex))
            {
                lock (_data.Lock)
                {
                    if (deleteIndex >= 0 && deleteIndex < _data.Data.quotes.Count)
                    {
                        removedQuote = _data.Data.quotes[deleteIndex];
                        _data.Data.quotes.RemoveAt(deleteIndex);
                        _data.Save();
                    }
                }
            }

            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                removedQuote != null ? $"🗑 Удалено: {removedQuote}" : "⚠️ Такой цитаты уже нет."
            );

            return;
        }

        var state = GetState(user.Id);
        var quote = state?.PendingQuote;

        if (state == null || quote == null)
        {
            await _bot.AnswerCallbackQueryAsync(query.Id);
            return;
        }

        if (query.Data == "regenerate" && state.Mode != UserMode.Edit && CanGenerate(user.Id))
        {
            if (!IsAdmin(user.Id) && !_rateLimiter.TryAcquire(user.Id))
            {
                await _bot.AnswerCallbackQueryAsync(query.Id, "⏳ Лимит генераций исчерпан. Попробуйте позже.", showAlert: true);
                return;
            }

            string regenerated;
            try
            {
                regenerated = await _ai.GenerateQuoteAsync();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Ошибка перегенерации цитаты через ИИ");
                await _bot.AnswerCallbackQueryAsync(query.Id, "⚠️ Не удалось перегенерировать цитату.", showAlert: true);
                return;
            }

            TrySetPendingQuote(user.Id, regenerated);

            var forAdd = state.Mode == UserMode.Add;

            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                GeneratedQuoteText(regenerated, forAdd),
                replyMarkup: GeneratedQuoteKeyboard(forAdd));

            await _bot.AnswerCallbackQueryAsync(query.Id);
            return;
        }

        if (query.Data == "confirm")
        {
            if (state.Mode == UserMode.Suggest)
            {
                var suggestion = new Suggestion
                {
                    user_id = user.Id,
                    name = user.Username ?? user.FirstName,
                    quote = quote
                };

                lock (_data.Lock)
                {
                    _data.Data.suggestions.Add(suggestion);
                    _data.Save();
                }

                foreach (var adminId in _adminIds)
                {
                    try
                    {
                        await _bot.SendTextMessageAsync(
                            adminId,
                            $"📩 Новое предложение от @{user.Username ?? user.FirstName}:\n\n{quote}",
                            replyMarkup: new InlineKeyboardMarkup(new[]
                            {
                                new[]
                                {
                                    InlineKeyboardButton.WithCallbackData("✅ Одобрить", $"approve_{suggestion.id}"),
                                    InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"reject_{suggestion.id}")
                                }
                            })
                        );
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Не удалось уведомить админа {AdminId} о новом предложении", adminId);
                    }
                }

                try
                {
                    await _bot.DeleteMessageAsync(query.Message!.Chat.Id, query.Message.MessageId);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Не удалось удалить сообщение с цитатой");
                }
            }

            if (state.Mode == UserMode.Add && IsAdmin(user.Id))
            {
                bool added;
                lock (_data.Lock)
                {
                    added = !_quotes.Exists(quote);
                    if (added)
                    {
                        _data.Data.quotes.Add(quote);
                        _data.Save();
                    }
                }

                if (added)
                {
                    await _bot.EditMessageTextAsync(
                        query.Message!.Chat.Id,
                        query.Message.MessageId,
                        "🔥 Цитата добавлена.");

                    await NotifyAdminsQuoteAdded(quote, user.Id, user.Username ?? user.FirstName);
                }
                else
                {
                    await _bot.EditMessageTextAsync(
                        query.Message!.Chat.Id,
                        query.Message.MessageId,
                        "⚠️ Такая цитата уже существует.");
                }
            }

            if (state.Mode == UserMode.Edit && IsAdmin(user.Id))
            {
                bool applied = false;
                var editIndex = state.EditIndex;

                if (editIndex >= 0)
                {
                    lock (_data.Lock)
                    {
                        if (editIndex < _data.Data.quotes.Count)
                        {
                            _data.Data.quotes[editIndex] = quote;
                            _data.Save();
                            applied = true;
                        }
                    }
                }

                await _bot.EditMessageTextAsync(
                    query.Message!.Chat.Id,
                    query.Message.MessageId,
                    applied
                        ? $"✏️ Цитата №{editIndex + 1} обновлена."
                        : "⚠️ Цитата больше не существует.");
            }
        }

        if (query.Data == "cancel")
        {
            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                "❌ Действие отменено.");
        }

        ClearState(user.Id);
    }

    private async Task NotifyAdminsQuoteAdded(string quote, long actorId, string actorLabel)
    {
        var text = $"🐺 Новая цитата в базе (добавил @{actorLabel}):\n\n{quote}";

        foreach (var adminId in _adminIds)
        {
            if (adminId == actorId)
                continue;

            try
            {
                await _bot.SendTextMessageAsync(adminId, text);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Не удалось уведомить админа {AdminId} о новой цитате", adminId);
            }
        }
    }

    private static InlineKeyboardMarkup RatingKeyboard(string hash, int likes, int dislikes) =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData($"👍 {likes}", $"rate_l_{hash}"),
                InlineKeyboardButton.WithCallbackData($"👎 {dislikes}", $"rate_d_{hash}")
            }
        });

    private static string GeneratedQuoteText(string quote, bool forAdd) =>
        $"🐺 Сгенерированная цитата:\n\n{quote}\n\n" +
        (forAdd ? "Добавить её в базу?" : "Предложить её на добавление?");

    private static InlineKeyboardMarkup GeneratedQuoteKeyboard(bool forAdd) =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData(forAdd ? "✅ Добавить" : "✅ Предложить", "confirm"),
                InlineKeyboardButton.WithCallbackData("🔄 Перегенерировать", "regenerate"),
                InlineKeyboardButton.WithCallbackData("❌ Отменить", "cancel")
            }
        });

    private void SetState(long userId, UserState state)
    {
        lock (_userStateLock)
        {
            PruneExpiredStates();
            _userState[userId] = state;
        }
    }

    private UserState? GetState(long userId)
    {
        lock (_userStateLock)
        {
            if (!_userState.TryGetValue(userId, out var state))
                return null;

            if (state.IsExpired(StateTtl))
            {
                _userState.Remove(userId);
                return null;
            }

            return state;
        }
    }

    private bool TrySetPendingQuote(long userId, string text)
    {
        lock (_userStateLock)
        {
            if (!_userState.TryGetValue(userId, out var state))
                return false;

            if (state.IsExpired(StateTtl))
            {
                _userState.Remove(userId);
                return false;
            }

            state.PendingQuote = text;
            return true;
        }
    }

    private void ClearState(long userId)
    {
        lock (_userStateLock)
        {
            _userState.Remove(userId);
        }
    }

    private void PruneExpiredStates()
    {
        var expired = _userState
            .Where(kv => kv.Value.IsExpired(StateTtl))
            .Select(kv => kv.Key)
            .ToList();

        foreach (var key in expired)
            _userState.Remove(key);
    }

    private Task Error(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        _logger.LogError(ex, "Ошибка Telegram-клиента");
        return Task.CompletedTask;
    }
}
