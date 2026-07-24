using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.InputFiles;
using Telegram.Bot.Types.Payments;
using Telegram.Bot.Types.ReplyMarkups;

class BotService
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromMinutes(30);
    private const string PaymentPayload = "quote_generation";

    private readonly ITelegramBotClient _bot;
    private readonly InlineHandler _inline;
    private readonly DataService _data;
    private readonly QuoteService _quotes;
    private readonly AiQuoteService _ai;
    private readonly HashSet<long> _adminIds;
    private readonly RateLimiter _rateLimiter;
    private readonly int _defaultStarsPrice;
    private readonly ILogger<BotService> _logger;

    private readonly object _userStateLock = new();
    private readonly Dictionary<long, UserState> _userState = new();

    public BotService(ITelegramBotClient bot, InlineHandler inline, DataService data, QuoteService quotes, AiQuoteService ai, IEnumerable<long> adminIds, RateLimiter rateLimiter, int starsPrice, ILogger<BotService> logger)
    {
        _bot = bot;
        _inline = inline;
        _data = data;
        _quotes = quotes;
        _ai = ai;
        _adminIds = new HashSet<long>(adminIds);
        _rateLimiter = rateLimiter;
        _defaultStarsPrice = starsPrice;
        _logger = logger;
    }

    // Текущая цена генерации в звёздах: значение из /setprice, если оно задано, иначе конфиг StarsPrice.
    private int StarsPrice => _data.StarsPrice(_defaultStarsPrice);

    public void Start()
    {
        TrySync(() => SetDefaultCommandsAsync().GetAwaiter().GetResult(),
            "Не удалось установить меню команд по умолчанию");

        var adminCommands = new[]
        {
            Cmd("quote", "Случайная цитата"),
            Cmd("suggest", "Предложить цитату"),
            Cmd("generate", "Сгенерировать цитату через ИИ"),
            Cmd("addquote", "Добавить цитату"),
            Cmd("editquote", "Редактировать цитату"),
            Cmd("deletequote", "Удалить цитату"),
            Cmd("listsuggest", "Список предложений"),
            Cmd("approve", "Принять предложение"),
            Cmd("reject", "Отклонить предложение"),
            Cmd("publicgen", "Вкл/выкл генерацию для всех"),
            Cmd("setprice", "Изменить цену генерации в звёздах"),
            Cmd("stats", "Статистика"),
            Cmd("export", "Экспорт базы цитат"),
            Cmd("list", "Список цитат"),
            Cmd("guide", "Подробный гайд по боту"),
            Cmd("help", "Показать помощь"),
        };

        foreach (var adminId in _adminIds)
            TrySync(() => _bot.SetMyCommandsAsync(adminCommands, scope: new BotCommandScopeChat { ChatId = adminId }).GetAwaiter().GetResult(),
                "Не удалось установить меню команд для админа {AdminId}", adminId);

        _bot.StartReceiving(Update, Error);
    }

    private static BotCommand Cmd(string command, string description) =>
        new() { Command = command, Description = description };

    // Best-effort вызов Telegram API: ошибка логируется, но не прерывает обработку.
    // Синхронная версия — для мест, где обвязка ещё не асинхронна (Start()).
    private void TrySync(Action action, string warningMessage, params object?[] args)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, warningMessage, args);
        }
    }

    private async Task TryAsync(Func<Task> action, string warningMessage, params object?[] args)
    {
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, warningMessage, args);
        }
    }

    // Меню команд для обычных пользователей; /generate показывается,
    // только пока открыта публичная генерация.
    private async Task SetDefaultCommandsAsync()
    {
        var commands = new List<BotCommand>
        {
            Cmd("quote", "Случайная цитата"),
            Cmd("suggest", "Предложить цитату"),
        };

        if (_data.AllowPublicGeneration)
            commands.Add(Cmd("generate", "Сгенерировать цитату через ИИ"));

        commands.Add(Cmd("list", "Список цитат"));
        commands.Add(Cmd("guide", "Подробный гайд по боту"));
        commands.Add(Cmd("help", "Показать помощь"));

        await _bot.SetMyCommandsAsync(commands);
    }

    private bool IsAdmin(long userId) => _adminIds.Contains(userId);

    private bool CanGenerate(long userId) => IsAdmin(userId) || _data.AllowPublicGeneration;

    private async Task Update(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        if (update.Type == UpdateType.InlineQuery) { await _inline.Handle(bot, update.InlineQuery!); return; }
        if (update.PreCheckoutQuery != null) { await HandlePreCheckout(update.PreCheckoutQuery); return; }
        if (update.Message?.SuccessfulPayment != null) { await HandleSuccessfulPayment(update.Message); return; }

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

        if (!text.StartsWith('/')) { await HandlePendingInput(chatId, user.Id, text); return; }

        var (command, args) = ParseCommand(text);

        switch (command)
        {
            case "/start": await HandleStart(chatId); break;
            case "/help": await HandleHelp(chatId, user.Id); break;
            case "/guide": await HandleGuide(chatId, user.Id); break;
            case "/list": await HandleList(chatId); break;
            case "/quote": await HandleQuote(chatId); break;
            case "/suggest": await HandleSuggest(chatId, user.Id); break;
            case "/paysupport": await HandlePaySupport(chatId); break;
            case "/generate" when CanGenerate(user.Id): await HandleGenerate(chatId, user.Id); break;
            case "/publicgen" when IsAdmin(user.Id): await HandleTogglePublicGeneration(chatId); break;
            case "/setprice" when IsAdmin(user.Id): await HandleSetPrice(chatId, args); break;
            case "/stats" when IsAdmin(user.Id): await HandleStats(chatId); break;
            case "/export" when IsAdmin(user.Id): await HandleExport(chatId); break;
            case "/addquote" when IsAdmin(user.Id): await HandleAddQuote(chatId, user.Id); break;
            case "/editquote" when IsAdmin(user.Id): await HandleEditQuote(chatId, user.Id, args); break;
            case "/deletequote" when IsAdmin(user.Id): await HandleDeleteQuote(chatId, args); break;
            case "/listsuggest" when IsAdmin(user.Id): await HandleListSuggest(chatId); break;
            case "/reject" when IsAdmin(user.Id): await HandleReject(chatId, args); break;
            case "/approve" when IsAdmin(user.Id): await HandleApprove(chatId, args, user); break;
            default: await HandlePendingInput(chatId, user.Id, text); break;
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
                "Общие команды:\n\n/help - список команд\n/start - запуск бота\n/quote - случайная цитата\n/suggest - предложить цитату\n/list - список цитат\nАдминские команды:\n\n/addquote - добавить цитату\n/editquote <номер> - редактировать цитату\n/deletequote <номер> - удалить цитату\n/generate - сгенерировать цитату через ИИ\n/publicgen - вкл/выкл генерацию для всех\n/setprice <число> - изменить цену генерации в звёздах\n/listsuggest - список предложений\n/approve - принять предложение\n/reject - отклонить предложение\n/stats - статистика\n/export - экспорт базы цитат\n\nВ inline-режиме (@botname ai) можно сгенерировать цитату через ИИ прямо в любом чате.");
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
                            "результат уходит в очередь предложений на модерацию. Лимит — 5 генераций в час; " +
                            "сверх лимита генерацию можно купить за звёзды Telegram (по вопросам оплаты — /paysupport).");
        }
        else
        {
            text.AppendLine("Сейчас генерация доступна только админам. Когда админы откроют её командой /publicgen, " +
                            "появится команда /generate: результат уходит на модерацию, лимит — 5 генераций в час, " +
                            "сверх лимита — за звёзды Telegram.");
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
            text.AppendLine($"/setprice <число> — изменить цену генерации сверх лимита в звёздах (сейчас {StarsPrice} ⭐, 0 отключает продажу; настройка переживает перезапуск);");
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
        var quotes = _data.Read(d => d.quotes.ToList());

        if (quotes.Count == 0) { await _bot.SendTextMessageAsync(chatId, "Цитат пока нет."); return; }

        await _bot.SendTextMessageAsync(chatId,
            string.Join("\n", quotes.Select((q, i) => $"{i + 1}. {q}")));
    }

    private async Task HandleQuote(long chatId)
    {
        var quote = _quotes.GetRandomQuote();

        if (quote == null) { await _bot.SendTextMessageAsync(chatId, "Цитат пока нет."); return; }

        var hash = _quotes.HashOf(quote);
        var (likes, dislikes) = _quotes.GetCounts(hash);

        await _bot.SendTextMessageAsync(chatId, quote,
            replyMarkup: RatingKeyboard(hash, likes, dislikes));
    }

    private async Task HandleStats(long chatId)
    {
        var (quotes, ratings, suggestionCount, publicGen) = _data.Read(d =>
            (d.quotes.ToList(), new Dictionary<string, QuoteRating>(d.ratings), d.suggestions.Count, d.allow_public_generation));

        var totalLikes = ratings.Values.Sum(r => r.likes.Count);
        var totalDislikes = ratings.Values.Sum(r => r.dislikes.Count);

        var text = "📊 Статистика:\n\n" +
                   $"Цитат в базе: {quotes.Count}\n" +
                   $"Предложений в очереди: {suggestionCount}\n" +
                   $"Генерация для всех: {(publicGen ? "включена" : "выключена")}\n" +
                   $"Голоса: 👍 {totalLikes} / 👎 {totalDislikes}";

        var top = quotes
            .Select(q => (Quote: q, Likes: ratings.TryGetValue(_quotes.HashOf(q), out var r) ? r.likes.Count : 0))
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
        var json = _data.Read(d => JsonSerializer.Serialize(d, new JsonSerializerOptions
        {
            WriteIndented = true
        }));

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
        // Сверх бесплатного лимита тратится оплаченная звёздами генерация,
        // а если её нет — пользователю выставляется счёт.
        var usedPaid = false;

        if (!IsAdmin(userId) && !_rateLimiter.TryAcquire(userId))
        {
            usedPaid = _data.TryConsumePaidGeneration(userId);

            if (!usedPaid) { await OfferPaidGeneration(chatId); return; }
        }

        var placeholder = await _bot.SendTextMessageAsync(chatId, "🐺 Генерирую цитату...");
        var generated = await GenerateOrNullAsync();

        if (generated == null)
        {
            if (usedPaid)
                _data.AddPaidGenerations(userId, 1);

            await _bot.EditMessageTextAsync(chatId, placeholder.MessageId,
                usedPaid
                    ? "⚠️ Не удалось сгенерировать цитату. Оплаченная генерация не потрачена — попробуйте /generate ещё раз."
                    : "⚠️ Не удалось сгенерировать цитату. Попробуйте позже.");
            return;
        }

        // Админ добавляет сразу в базу, обычный пользователь — через очередь предложений.
        var forAdd = IsAdmin(userId);

        SetState(userId, new UserState
        {
            Mode = forAdd ? UserMode.Add : UserMode.Suggest,
            PendingQuote = generated
        });

        await _bot.EditMessageTextAsync(chatId, placeholder.MessageId,
            GeneratedQuoteText(generated, forAdd),
            replyMarkup: GeneratedQuoteKeyboard(forAdd));
    }

    private async Task OfferPaidGeneration(long chatId)
    {
        var price = StarsPrice;

        if (price <= 0) { await _bot.SendTextMessageAsync(chatId, "⏳ Лимит генераций исчерпан. Попробуйте позже."); return; }

        await _bot.SendInvoiceAsync(
            chatId,
            "Генерация цитаты",
            $"Бесплатный лимит исчерпан. Одна генерация цитаты через ИИ — {price} ⭐.",
            PaymentPayload,
            providerToken: "", // для Telegram Stars токен провайдера не нужен
            currency: "XTR",
            prices: new[] { new LabeledPrice("Генерация цитаты", price) });
    }

    private async Task HandleSetPrice(long chatId, string args)
    {
        if (!TryParseIndex(args, out int price) || price < 0)
        {
            await _bot.SendTextMessageAsync(chatId, $"Использование: /setprice <число звёзд>\nТекущая цена: {StarsPrice} ⭐ (0 — продажа отключена).");
            return;
        }

        _data.SetStarsPrice(price);

        await _bot.SendTextMessageAsync(chatId,
            price == 0
                ? "🔒 Продажа генераций за звёзды отключена."
                : $"💫 Цена генерации сверх лимита установлена: {price} ⭐.");
    }

    private async Task HandlePreCheckout(PreCheckoutQuery query)
    {
        if (query.InvoicePayload == PaymentPayload)
            await _bot.AnswerPreCheckoutQueryAsync(query.Id);
        else
            await _bot.AnswerPreCheckoutQueryAsync(query.Id, "Неизвестный платёж, попробуйте ещё раз.");
    }

    private async Task HandleSuccessfulPayment(Message message)
    {
        var payment = message.SuccessfulPayment!;
        var userId = message.From!.Id;

        _logger.LogInformation("Оплата {Amount} XTR от {UserId}, charge id {ChargeId}",
            payment.TotalAmount, userId, payment.TelegramPaymentChargeId);

        // Начисляем генерацию и сразу запускаем её; если генерация не удастся,
        // оплаченная попытка вернётся и сработает при следующем /generate.
        _data.AddPaidGenerations(userId, 1);
        await HandleGenerate(message.Chat.Id, userId);
    }

    private async Task HandlePaySupport(long chatId)
    {
        await _bot.SendTextMessageAsync(chatId,
            "💫 По вопросам оплаты и возврата звёзд напишите админам бота — контакты в описании. " +
            "Если оплаченная генерация не сработала, она сохраняется и потратится при следующем /generate.");
    }

    private async Task<string?> GenerateOrNullAsync()
    {
        try
        {
            return await _ai.GenerateQuoteAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка генерации цитаты через ИИ");
            return null;
        }
    }

    private async Task HandleTogglePublicGeneration(long chatId)
    {
        var enabled = _data.TogglePublicGeneration();

        await TryAsync(SetDefaultCommandsAsync,
            "Не удалось обновить меню команд после переключения публичной генерации");

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

    private string? GetQuoteAt(int index) =>
        _data.Read(d => index >= 0 && index < d.quotes.Count ? d.quotes[index] : null);

    private async Task HandleEditQuote(long chatId, long userId, string args)
    {
        if (!TryParseIndex(args, out int index)) { await _bot.SendTextMessageAsync(chatId, "Использование: /editquote <номер>"); return; }

        index -= 1;
        var current = GetQuoteAt(index);

        if (current == null) { await _bot.SendTextMessageAsync(chatId, "Неверный номер цитаты"); return; }

        SetState(userId, new UserState { Mode = UserMode.Edit, EditIndex = index });

        await _bot.SendTextMessageAsync(chatId,
            $"✏️ Текущая цитата №{index + 1}:\n\n{current}\n\nВведите новый текст.");
    }

    private async Task HandleDeleteQuote(long chatId, string args)
    {
        if (!TryParseIndex(args, out int index)) { await _bot.SendTextMessageAsync(chatId, "Использование: /deletequote <номер>"); return; }

        index -= 1;
        var current = GetQuoteAt(index);

        if (current == null) { await _bot.SendTextMessageAsync(chatId, "Неверный номер цитаты"); return; }

        await _bot.SendTextMessageAsync(chatId,
            $"Удалить цитату №{index + 1}?\n\n{current}",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("🗑 Удалить", $"delquote_{index}"),
                InlineKeyboardButton.WithCallbackData("❌ Отменить", "delquote_cancel")
            }));
    }

    private async Task HandleListSuggest(long chatId)
    {
        var suggestions = _data.Read(d => d.suggestions.ToList());

        if (suggestions.Count == 0) { await _bot.SendTextMessageAsync(chatId, "Нет предложенных цитат"); return; }

        await _bot.SendTextMessageAsync(chatId, string.Join("\n",
            suggestions.Select((s, i) => $"{i + 1}. {s.quote} (от {s.name})")));
    }

    private async Task HandleReject(long chatId, string args)
    {
        if (!TryParseIndex(args, out int index)) { await _bot.SendTextMessageAsync(chatId, "Использование: /reject <номер>"); return; }

        var removed = _data.RemoveSuggestion(_ => index - 1, approve: false, _quotes.Exists);

        await _bot.SendTextMessageAsync(chatId,
            removed != null ? $"❌ Отклонено: {removed.quote}" : "Неверный номер предложения");
    }

    private async Task HandleApprove(long chatId, string args, User user)
    {
        if (!TryParseIndex(args, out int index)) { await _bot.SendTextMessageAsync(chatId, "Использование: /approve <номер>"); return; }

        var suggestion = _data.RemoveSuggestion(_ => index - 1, approve: true, _quotes.Exists);

        if (suggestion == null) { await _bot.SendTextMessageAsync(chatId, "Неверный номер предложения"); return; }

        await _bot.SendTextMessageAsync(chatId, $"✅ Добавлено: {suggestion.quote}");
        await NotifyAdminsQuoteAdded(suggestion.quote, user.Id, user.Username ?? user.FirstName);
    }

    private async Task HandlePendingInput(long chatId, long userId, string text)
    {
        if (!TrySetPendingQuote(userId, text))
            return;

        await _bot.SendTextMessageAsync(chatId,
            $"Вот что вы ввели:\n\n{text}\n\nПодтвердить?",
            replyMarkup: new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Подтвердить", "confirm"),
                InlineKeyboardButton.WithCallbackData("❌ Отменить", "cancel")
            }));
    }

    private async Task HandleCallback(Update update)
    {
        var query = update.CallbackQuery!;
        var user = query.From;
        var data = query.Data;

        if (data != null && (data.StartsWith("rate_l_") || data.StartsWith("rate_d_"))) { await HandleRatingCallback(query, user, data); return; }
        if (data != null && data.StartsWith("approve_") && IsAdmin(user.Id)) { await HandleApproveCallback(query, user, data); return; }
        if (data != null && data.StartsWith("reject_") && IsAdmin(user.Id)) { await HandleRejectCallback(query, data); return; }
        if (data != null && data.StartsWith("delquote_") && IsAdmin(user.Id)) { await HandleDeleteQuoteCallback(query, data); return; }

        await HandlePendingStateCallback(query, user);
    }

    private async Task HandleRatingCallback(CallbackQuery query, User user, string data)
    {
        var like = data.StartsWith("rate_l_");
        var hash = data.Substring("rate_l_".Length);

        var counts = _quotes.Vote(hash, user.Id, like);

        if (counts == null) { await _bot.AnswerCallbackQueryAsync(query.Id, "⚠️ Этой цитаты больше нет."); return; }

        await _bot.EditMessageReplyMarkupAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            RatingKeyboard(hash, counts.Value.Likes, counts.Value.Dislikes));

        await _bot.AnswerCallbackQueryAsync(query.Id, "Голос учтён");
    }

    private async Task HandleApproveCallback(CallbackQuery query, User user, string data)
    {
        var id = data.Substring("approve_".Length);
        var suggestion = _data.RemoveSuggestion(list => list.FindIndex(s => s.id == id), approve: true, _quotes.Exists);

        await _bot.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            suggestion != null ? "✅ Цитата одобрена и добавлена." : "⚠️ Это предложение уже обработано.");

        if (suggestion != null)
            await NotifyAdminsQuoteAdded(suggestion.quote, user.Id, user.Username ?? user.FirstName);
    }

    private async Task HandleRejectCallback(CallbackQuery query, string data)
    {
        var id = data.Substring("reject_".Length);
        var removed = _data.RemoveSuggestion(list => list.FindIndex(s => s.id == id), approve: false, _quotes.Exists);

        await _bot.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            removed != null ? "❌ Цитата отклонена." : "⚠️ Это предложение уже обработано.");
    }

    private async Task HandleDeleteQuoteCallback(CallbackQuery query, string data)
    {
        var rest = data.Substring("delquote_".Length);

        if (rest == "cancel") { await _bot.EditMessageTextAsync(query.Message!.Chat.Id, query.Message.MessageId, "❌ Удаление отменено."); return; }

        string? removedQuote = int.TryParse(rest, out int deleteIndex)
            ? _data.TryRemoveQuoteAt(deleteIndex)
            : null;

        await _bot.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            removedQuote != null ? $"🗑 Удалено: {removedQuote}" : "⚠️ Такой цитаты уже нет.");
    }

    // Обрабатывает regenerate/confirm/cancel — колбэки, зависящие от UserState
    // текущего пользователя (созданного /suggest, /addquote, /editquote или /generate).
    private async Task HandlePendingStateCallback(CallbackQuery query, User user)
    {
        var state = GetState(user.Id);
        var quote = state?.PendingQuote;

        if (state == null || quote == null) { await _bot.AnswerCallbackQueryAsync(query.Id); return; }

        if (query.Data == "regenerate" && state.Mode != UserMode.Edit && CanGenerate(user.Id)) { await HandleRegenerateCallback(query, user, state); return; }

        if (query.Data == "confirm")
        {
            if (state.Mode == UserMode.Suggest)
                await HandleConfirmSuggest(query, user, quote);

            if (state.Mode == UserMode.Add && IsAdmin(user.Id))
                await HandleConfirmAdd(query, user, quote);

            if (state.Mode == UserMode.Edit && IsAdmin(user.Id))
                await HandleConfirmEdit(query, state.EditIndex, quote);
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

    private async Task HandleRegenerateCallback(CallbackQuery query, User user, UserState state)
    {
        var usedPaid = false;

        if (!IsAdmin(user.Id) && !_rateLimiter.TryAcquire(user.Id))
        {
            usedPaid = _data.TryConsumePaidGeneration(user.Id);

            if (!usedPaid) { await OfferPaidGeneration(query.Message!.Chat.Id); await _bot.AnswerCallbackQueryAsync(query.Id); return; }
        }

        var regenerated = await GenerateOrNullAsync();

        if (regenerated == null)
        {
            if (usedPaid)
                _data.AddPaidGenerations(user.Id, 1);

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
    }

    private async Task HandleConfirmSuggest(CallbackQuery query, User user, string quote)
    {
        var suggestion = new Suggestion
        {
            user_id = user.Id,
            name = user.Username ?? user.FirstName,
            quote = quote
        };

        _data.AddSuggestion(suggestion);

        await SendToAdminsAsync(
            $"📩 Новое предложение от @{user.Username ?? user.FirstName}:\n\n{quote}",
            new InlineKeyboardMarkup(new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Одобрить", $"approve_{suggestion.id}"),
                InlineKeyboardButton.WithCallbackData("❌ Отклонить", $"reject_{suggestion.id}")
            }));

        await TryAsync(() => _bot.DeleteMessageAsync(query.Message!.Chat.Id, query.Message.MessageId),
            "Не удалось удалить сообщение с цитатой");
    }

    private async Task HandleConfirmAdd(CallbackQuery query, User user, string quote)
    {
        var added = _data.TryAddQuote(quote, _quotes.Exists);

        await _bot.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            added ? "🔥 Цитата добавлена." : "⚠️ Такая цитата уже существует.");

        if (added)
            await NotifyAdminsQuoteAdded(quote, user.Id, user.Username ?? user.FirstName);
    }

    private async Task HandleConfirmEdit(CallbackQuery query, int editIndex, string quote)
    {
        var applied = _data.TryEditQuoteAt(editIndex, quote);

        await _bot.EditMessageTextAsync(
            query.Message!.Chat.Id,
            query.Message.MessageId,
            applied ? $"✏️ Цитата №{editIndex + 1} обновлена." : "⚠️ Цитата больше не существует.");
    }

    private async Task SendToAdminsAsync(string text, InlineKeyboardMarkup? keyboard = null, long? excludeId = null)
    {
        foreach (var adminId in _adminIds)
        {
            if (adminId == excludeId)
                continue;

            await TryAsync(() => _bot.SendTextMessageAsync(adminId, text, replyMarkup: keyboard),
                "Не удалось отправить сообщение админу {AdminId}", adminId);
        }
    }

    private Task NotifyAdminsQuoteAdded(string quote, long actorId, string actorLabel) =>
        SendToAdminsAsync($"🐺 Новая цитата в базе (добавил @{actorLabel}):\n\n{quote}", excludeId: actorId);

    private static InlineKeyboardMarkup RatingKeyboard(string hash, int likes, int dislikes) =>
        new(new[]
        {
            InlineKeyboardButton.WithCallbackData($"👍 {likes}", $"rate_l_{hash}"),
            InlineKeyboardButton.WithCallbackData($"👎 {dislikes}", $"rate_d_{hash}")
        });

    private static string GeneratedQuoteText(string quote, bool forAdd) =>
        $"🐺 Сгенерированная цитата:\n\n{quote}\n\n" +
        (forAdd ? "Добавить её в базу?" : "Предложить её на добавление?");

    private static InlineKeyboardMarkup GeneratedQuoteKeyboard(bool forAdd) =>
        new(new[]
        {
            InlineKeyboardButton.WithCallbackData(forAdd ? "✅ Добавить" : "✅ Предложить", "confirm"),
            InlineKeyboardButton.WithCallbackData("🔄 Перегенерировать", "regenerate"),
            InlineKeyboardButton.WithCallbackData("❌ Отменить", "cancel")
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

            if (state.IsExpired(StateTtl)) { _userState.Remove(userId); return null; }

            return state;
        }
    }

    private bool TrySetPendingQuote(long userId, string text)
    {
        lock (_userStateLock)
        {
            var state = GetState(userId);
            if (state == null)
                return false;

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
