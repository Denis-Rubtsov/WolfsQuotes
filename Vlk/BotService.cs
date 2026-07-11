using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

class BotService
{
    private readonly ITelegramBotClient _bot;
    private readonly InlineHandler _inline;
    private readonly DataService _data;
    private readonly QuoteService _quotes;
    private readonly AiQuoteService _ai;
    private readonly HashSet<long> _adminIds;
    private readonly string _voiceUrl;

    private readonly object _userStateLock = new();
    private readonly Dictionary<long, Dictionary<string, string>> _userState = new();

    public BotService(ITelegramBotClient bot, InlineHandler inline, DataService data, QuoteService quotes, AiQuoteService ai, IEnumerable<long> adminIds, string voiceUrl)
    {
        _bot = bot;
        _inline = inline;
        _data = data;
        _quotes = quotes;
        _ai = ai;
        _adminIds = new HashSet<long>(adminIds);
        _voiceUrl = voiceUrl;
    }

    public void Start()
    {
        _bot.SetMyCommandsAsync(new[]
        {
            new BotCommand { Command = "suggest", Description = "Предложить цитату" },
            new BotCommand { Command = "list", Description = "Список цитат" },
            new BotCommand { Command = "help", Description = "Показать помощь" },
        }).GetAwaiter().GetResult();

        _bot.StartReceiving(Update, Error);
    }

    private bool IsAdmin(long userId) => _adminIds.Contains(userId);

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

        if (text.StartsWith("/start"))
        {
            await _bot.SendTextMessageAsync(chatId,
                "Добро пожаловать в бот \"Вълчьи цитаты\".\n\n/help - список команд\n/start - запуск бота\n/suggest — предложить цитату\n/list — список цитат");
            return;
        }

        if (text.StartsWith("/help"))
        {
            if (IsAdmin(user.Id))
            {
                await _bot.SendTextMessageAsync(chatId,
                    "Общие команды:\n\n/help - список команд\n/start - запуск бота\n/suggest - предложить цитату\n/list - список цитат\nАдминские команды:\n\n/addquote - добавить цитату\n/editquote <номер> - редактировать цитату\n/deletequote <номер> - удалить цитату\n/generate - сгенерировать цитату через ИИ\n/listsuggest - список предложений\n/approve - принять предложение\n/reject - отклонить предложение");
                return;
            }
            await _bot.SendTextMessageAsync(chatId,
                "Список команд:\n\n/help - список команд\n/start - запуск бота\n/suggest - предложить цитату\n/list - список цитат\n");
            return;
        }

        if (text.StartsWith("/list") && !text.StartsWith("/listsuggest"))
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
            return;
        }

        if (text.StartsWith("/suggest"))
        {
            SetMode(user.Id, "suggest");
            await _bot.SendTextMessageAsync(chatId, "✍️ Введите цитату для предложения.");
            return;
        }

        if (text.StartsWith("/generate") && IsAdmin(user.Id))
        {
            var placeholder = await _bot.SendTextMessageAsync(chatId, "🐺 Генерирую цитату...");

            string generated;
            try
            {
                generated = await _ai.GenerateQuoteAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка генерации цитаты через ИИ: {ex}");
                await _bot.EditMessageTextAsync(chatId, placeholder.MessageId,
                    "⚠️ Не удалось сгенерировать цитату. Попробуйте позже.");
                return;
            }

            lock (_userStateLock)
            {
                _userState[user.Id] = new Dictionary<string, string>
                {
                    ["mode"] = "add",
                    ["pending_quote"] = generated
                };
            }

            await _bot.EditMessageTextAsync(
                chatId,
                placeholder.MessageId,
                $"🐺 Сгенерированная цитата:\n\n{generated}\n\nДобавить её в базу?",
                replyMarkup: GeneratedQuoteKeyboard());
            return;
        }

        if (text.StartsWith("/addquote") && IsAdmin(user.Id))
        {
            SetMode(user.Id, "add");
            await _bot.SendTextMessageAsync(chatId, "Введите цитату для добавления.");
            return;
        }

        if (text.StartsWith("/editquote") && IsAdmin(user.Id))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
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

            lock (_userStateLock)
            {
                _userState[user.Id] = new Dictionary<string, string>
                {
                    ["mode"] = "edit",
                    ["edit_index"] = index.ToString()
                };
            }

            await _bot.SendTextMessageAsync(chatId,
                $"✏️ Текущая цитата №{index + 1}:\n\n{current}\n\nВведите новый текст.");
            return;
        }

        if (text.StartsWith("/deletequote") && IsAdmin(user.Id))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
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
            return;
        }

        if (text.StartsWith("/listsuggest") && IsAdmin(user.Id))
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
            return;
        }


        if (text.StartsWith("/reject") && IsAdmin(user.Id))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
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

            return;
        }

        if (text.StartsWith("/approve") && IsAdmin(user.Id))
        {
            var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length < 2 || !int.TryParse(parts[1], out int index))
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

            return;
        }

        string? pendingQuote = null;
        lock (_userStateLock)
        {
            if (_userState.ContainsKey(user.Id))
            {
                _userState[user.Id]["pending_quote"] = text;
                pendingQuote = text;
            }
        }

        if (pendingQuote == null)
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
            $"Вот что вы ввели:\n\n{pendingQuote}\n\nПодтвердить?",
            replyMarkup: keyboard);
    }

    private async Task HandleCallback(Update update)
    {
        var query = update.CallbackQuery!;
        var user = query.From;

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

        string? quote = null;
        string? mode = null;
        string? editIndexRaw = null;

        lock (_userStateLock)
        {
            if (_userState.TryGetValue(user.Id, out var state)
                && state.TryGetValue("pending_quote", out var pendingQuote)
                && state.TryGetValue("mode", out var pendingMode))
            {
                quote = pendingQuote;
                mode = pendingMode;
                state.TryGetValue("edit_index", out editIndexRaw);
            }
        }

        if (quote == null || mode == null)
        {
            await _bot.AnswerCallbackQueryAsync(query.Id);
            return;
        }

        if (query.Data == "regenerate" && mode == "add")
        {
            string regenerated;
            try
            {
                regenerated = await _ai.GenerateQuoteAsync();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Ошибка перегенерации цитаты через ИИ: {ex}");
                await _bot.AnswerCallbackQueryAsync(query.Id, "⚠️ Не удалось перегенерировать цитату.", showAlert: true);
                return;
            }

            lock (_userStateLock)
            {
                _userState[user.Id]["pending_quote"] = regenerated;
            }

            await _bot.EditMessageTextAsync(
                query.Message!.Chat.Id,
                query.Message.MessageId,
                $"🐺 Сгенерированная цитата:\n\n{regenerated}\n\nДобавить её в базу?",
                replyMarkup: GeneratedQuoteKeyboard());

            await _bot.AnswerCallbackQueryAsync(query.Id);
            return;
        }

        if (query.Data == "confirm")
        {
            if (mode == "suggest")
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
                        Console.WriteLine($"Не удалось уведомить админа {adminId} о новом предложении: {ex}");
                    }
                }

                try
                {
                    await _bot.DeleteMessageAsync(query.Message!.Chat.Id, query.Message.MessageId);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Не удалось удалить сообщение с цитатой: {ex}");
                }
            }

            if (mode == "add" && IsAdmin(user.Id))
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
                }
                else
                {
                    await _bot.EditMessageTextAsync(
                        query.Message!.Chat.Id,
                        query.Message.MessageId,
                        "⚠️ Такая цитата уже существует.");
                }
            }

            if (mode == "edit" && IsAdmin(user.Id))
            {
                bool applied = false;
                int editIndex = -1;

                if (editIndexRaw != null && int.TryParse(editIndexRaw, out editIndex))
                {
                    lock (_data.Lock)
                    {
                        if (editIndex >= 0 && editIndex < _data.Data.quotes.Count)
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

        lock (_userStateLock)
        {
            _userState.Remove(user.Id);
        }
    }

    private static InlineKeyboardMarkup GeneratedQuoteKeyboard() =>
        new(new[]
        {
            new[]
            {
                InlineKeyboardButton.WithCallbackData("✅ Добавить", "confirm"),
                InlineKeyboardButton.WithCallbackData("🔄 Перегенерировать", "regenerate"),
                InlineKeyboardButton.WithCallbackData("❌ Отменить", "cancel")
            }
        });

    private void SetMode(long userId, string mode)
    {
        lock (_userStateLock)
        {
            _userState[userId] = new Dictionary<string, string>
            {
                ["mode"] = mode
            };
        }
    }

    private Task Error(ITelegramBotClient bot, Exception ex, CancellationToken ct)
    {
        Console.WriteLine(ex);
        return Task.CompletedTask;
    }
}
