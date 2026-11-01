using Microsoft.Extensions.Logging;
using OpenAI.Chat;

class AiQuoteService
{
    private const string DefaultSystemPrompt =
        "Ты помогаешь придумывать короткие абсурдные афористичные цитаты для телеграм-бота \"Вълчьи цитаты\" " +
        "от лица мудрого и харизматичного Волка. Пиши на русском языке. Одна цитата, 1-2 предложения, " +
        "без кавычек и подписи, в философско-ироничном тоне. Вместо \"волк\" пиши \"вълкъ\". В ответе — только сама цитата.";

    private readonly ChatClient _client;
    private readonly DataService _data;
    private readonly string? _promptFile;
    private readonly S3Storage? _s3;
    private readonly string _promptKey;
    // Последний промпт, полученный из S3, вместе с его ETag (одной ссылкой — генерации идут параллельно)
    private volatile PromptCache _s3Prompt = new(null, null);
    private record PromptCache(string? Text, string? ETag);
    private readonly ILogger<AiQuoteService> _logger;
    private readonly Random _random = new();

    public AiQuoteService(string apiKey, string? promptFile, S3Storage? s3, string promptKey, DataService data, ILogger<AiQuoteService> logger)
    {
        _client = new ChatClient("gpt-4o-mini", apiKey);
        _promptFile = promptFile;
        _s3 = s3;
        _promptKey = promptKey;
        _data = data;
        _logger = logger;
    }

    public async Task<string> GenerateQuoteAsync()
    {
        var examples = _data.Read(d => d.quotes.OrderBy(_ => _random.Next()).Take(10).ToList());

        var systemPrompt = _s3 != null ? await LoadSystemPromptFromS3Async() : LoadSystemPrompt();

        var userPrompt = examples.Count > 0
            ? "Вот примеры цитат в нужном стиле:\n" + string.Join("\n", examples.Select(q => $"- {q}")) +
              "\n\nПридумай новую цитату в таком же стиле."
            : "Придумай мудрую цитату от лица Волка.";

        var options = new ChatCompletionOptions
        {
            Temperature = 1.1f
        };

        ChatCompletion completion = await _client.CompleteChatAsync(
            new ChatMessage[]
            {
                new SystemChatMessage(systemPrompt),
                new UserChatMessage(userPrompt)
            },
            options);

        return completion.Content[0].Text.Trim().Trim('"');
    }

    // Промпт из S3 проверяется на каждую генерацию, но по ETag: если не менялся, тело не скачивается.
    // Если S3 недоступен — последний полученный промпт, если его не было — встроенный.
    private async Task<string> LoadSystemPromptFromS3Async()
    {
        try
        {
            var (found, notModified, text, etag) = await _s3!.GetIfChangedAsync(_promptKey, _s3Prompt.ETag);
            if (!found)
            {
                _logger.LogWarning("Промпта {Key} нет в S3, используется промпт по умолчанию", _promptKey);
                _s3Prompt = new(null, null);
            }
            else if (!notModified)
                _s3Prompt = new(string.IsNullOrWhiteSpace(text) ? null : text.Trim(), etag);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Не удалось получить промпт {Key} из S3 ({Error}), используется последний полученный", _promptKey, ex.Message);
        }

        return _s3Prompt.Text ?? DefaultSystemPrompt;
    }

    private string LoadSystemPrompt()
    {
        if (string.IsNullOrWhiteSpace(_promptFile)) return DefaultSystemPrompt;

        try
        {
            if (File.Exists(_promptFile))
            {
                var text = File.ReadAllText(_promptFile).Trim();
                if (text.Length > 0) return text;
            }

            _logger.LogWarning("Файл системного промпта {PromptFile} не найден или пуст, используется промпт по умолчанию", _promptFile);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось прочитать файл системного промпта {PromptFile}, используется промпт по умолчанию", _promptFile);
        }

        return DefaultSystemPrompt;
    }
}
