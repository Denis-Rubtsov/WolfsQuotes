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
    private readonly ILogger<AiQuoteService> _logger;
    private readonly Random _random = new();

    public AiQuoteService(string apiKey, string? promptFile, DataService data, ILogger<AiQuoteService> logger)
    {
        _client = new ChatClient("gpt-4o-mini", apiKey);
        _promptFile = promptFile;
        _data = data;
        _logger = logger;
    }

    public async Task<string> GenerateQuoteAsync()
    {
        List<string> examples;
        lock (_data.Lock)
        {
            examples = _data.Data.quotes
                .OrderBy(_ => _random.Next())
                .Take(10)
                .ToList();
        }

        var systemPrompt = LoadSystemPrompt();

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

    private string LoadSystemPrompt()
    {
        if (string.IsNullOrWhiteSpace(_promptFile))
            return DefaultSystemPrompt;

        try
        {
            if (File.Exists(_promptFile))
            {
                var text = File.ReadAllText(_promptFile).Trim();
                if (text.Length > 0)
                    return text;
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
