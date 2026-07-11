using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.InlineQueryResults;

class InlineHandler
{
    private readonly DataService _data;
    private readonly AiQuoteService _ai;
    private readonly HashSet<long> _adminIds;
    private readonly string _voiceUrl;
    private readonly Random _random = new();

    public InlineHandler(QuoteService quotes, DataService data, AiQuoteService ai, IEnumerable<long> adminIds, string voiceUrl)
    {
        _data = data;
        _ai = ai;
        _adminIds = new HashSet<long>(adminIds);
        _voiceUrl = voiceUrl;
    }

    public async Task Handle(ITelegramBotClient bot, InlineQuery query)
    {
        try
        {
            var input = query.Query.Trim();

            if (input.Equals("ai", StringComparison.OrdinalIgnoreCase))
            {
                await HandleAiGeneration(bot, query);
                return;
            }

            List<string> quotesSnapshot;
            lock (_data.Lock)
            {
                quotesSnapshot = new List<string>(_data.Data.quotes);
            }

            var quoteCount = quotesSnapshot.Count;

            if (quoteCount == 0)
            {
                await bot.AnswerInlineQueryAsync(
                    query.Id,
                    Array.Empty<InlineQueryResult>(),
                    cacheTime: 0,
                    isPersonal: true,
                    null,
                    switchPmText: "Цитат пока нет",
                    "start"
                );
                return;
            }

            string quote;
            string title = "Вспомнить мудрость";
            int index;

            if (int.TryParse(input, out index))
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
                        switchPmText: $"Введите целое число от 1 до {quoteCount}",
                        "start"
                    );
                    return;
                }

                quote = quotesSnapshot[index];
                title = $"Мудрость №{index + 1}";
            }
            else
            {
                index = _random.Next(quoteCount);
                quote = quotesSnapshot[index];
            }

            var voice = _voiceUrl + $"{index}.ogg";

            var results = new InlineQueryResult[]
            {
                new InlineQueryResultArticle(
                    Guid.NewGuid().ToString() + DateTime.UtcNow.Ticks,
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
                switchPmText: $"Введите целое число от 1 до {quoteCount}\nГолосовые цитаты временно не работают",
                "start"
            );
        }
        catch (Telegram.Bot.Exceptions.ApiRequestException ex)
            when (ex.Message.Contains("query is too old"))
        {
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex);
        }
    }

    private async Task HandleAiGeneration(ITelegramBotClient bot, InlineQuery query)
    {
        if (!_adminIds.Contains(query.From.Id))
        {
            await bot.AnswerInlineQueryAsync(
                query.Id,
                Array.Empty<InlineQueryResult>(),
                cacheTime: 0,
                isPersonal: true,
                null,
                switchPmText: "Генерация через ИИ доступна только админам",
                "start"
            );
            return;
        }

        string generated;
        try
        {
            generated = await _ai.GenerateQuoteAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Ошибка генерации цитаты через ИИ в inline-режиме: {ex}");
            await bot.AnswerInlineQueryAsync(
                query.Id,
                Array.Empty<InlineQueryResult>(),
                cacheTime: 0,
                isPersonal: true,
                null,
                switchPmText: "Не удалось сгенерировать цитату",
                "start"
            );
            return;
        }

        var results = new InlineQueryResult[]
        {
            new InlineQueryResultArticle(
                Guid.NewGuid().ToString(),
                "🐺 Цитата от ИИ",
                new InputTextMessageContent(generated))
            {
                Description = generated[..Math.Min(80, generated.Length)]
            }
        };

        await bot.AnswerInlineQueryAsync(
            query.Id,
            results,
            cacheTime: 0,
            isPersonal: true,
            null,
            switchPmText: "Введите \"ai\" ещё раз для новой генерации",
            "start"
        );
    }
}
