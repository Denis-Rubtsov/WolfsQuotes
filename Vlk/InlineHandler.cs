using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;

class InlineHandler
{
    private readonly DataService _data;
    private readonly AiQuoteService _ai;
    private readonly HashSet<long> _adminIds;
    private readonly string _voiceUrl;
    private readonly RateLimiter _rateLimiter;
    private readonly ILogger<InlineHandler> _logger;
    private readonly Random _random = new();

    public InlineHandler(DataService data, AiQuoteService ai, IEnumerable<long> adminIds, string voiceUrl, RateLimiter rateLimiter, ILogger<InlineHandler> logger)
    {
        _data = data;
        _ai = ai;
        _adminIds = new HashSet<long>(adminIds);
        _voiceUrl = voiceUrl;
        _rateLimiter = rateLimiter;
        _logger = logger;
    }

    public async Task Handle(ITelegramBotClient bot, InlineQuery query)
    {
        try
        {
            var input = query.Query.Trim();

            if (input.Equals("ai", StringComparison.OrdinalIgnoreCase)) { await HandleAiGeneration(bot, query); return; }

            var quotes = _data.Read(d => d.quotes.ToList());

            if (quotes.Count == 0) { await AnswerAsync(bot, query, "Цитат пока нет"); return; }

            var title = "Вспомнить мудрость";

            if (int.TryParse(input, out int index))
            {
                index -= 1;

                if (index < 0 || index >= quotes.Count) { await AnswerAsync(bot, query, $"Введите целое число от 1 до {quotes.Count}"); return; }

                title = $"Мудрость №{index + 1}";
            }
            else
            {
                index = _random.Next(quotes.Count);
            }

            await AnswerAsync(bot, query, $"Введите целое число от 1 до {quotes.Count}",
                Article(title, quotes[index]),
                new InlineQueryResultVoice(
                    Guid.NewGuid().ToString(),
                    _voiceUrl + $"{index}.ogg",
                    title + " голосом Волка"));
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            when (ex.Message.Contains("query is too old"))
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка обработки inline-запроса");
        }
    }

    private async Task HandleAiGeneration(ITelegramBotClient bot, InlineQuery query)
    {
        var userId = query.From.Id;
        var isAdmin = _adminIds.Contains(userId);

        if (!isAdmin && !_data.AllowPublicGeneration) { await AnswerAsync(bot, query, "Генерация через ИИ доступна только админам"); return; }

        // Сверх бесплатного лимита тратится оплаченная звёздами генерация;
        // купить её можно через /generate в личке бота.
        var usedPaid = false;

        if (!isAdmin && !_rateLimiter.TryAcquire(userId))
        {
            usedPaid = _data.TryConsumePaidGeneration(userId);

            if (!usedPaid) { await AnswerAsync(bot, query, "Лимит исчерпан — купить генерацию: /generate в ЛС бота"); return; }
        }

        string generated;
        try
        {
            generated = await _ai.GenerateQuoteAsync();
        }
        catch (Exception ex)
        {
            if (usedPaid)
                _data.AddPaidGenerations(userId, 1);

            _logger.LogError(ex, "Ошибка генерации цитаты через ИИ в inline-режиме");
            await AnswerAsync(bot, query, "Не удалось сгенерировать цитату");
            return;
        }

        await AnswerAsync(bot, query, "Введите \"ai\" ещё раз для новой генерации",
            Article("🐺 Цитата от ИИ", generated));
    }

    private static InlineQueryResultArticle Article(string title, string text) =>
        new(Guid.NewGuid().ToString(), title, new InputTextMessageContent(text))
        {
            Description = text[..Math.Min(80, text.Length)]
        };

    private static Task AnswerAsync(ITelegramBotClient bot, InlineQuery query, string switchPmText, params InlineQueryResult[] results) =>
        bot.AnswerInlineQueryAsync(query.Id, results, cacheTime: 0, isPersonal: true, null, switchPmText, "start");
}
