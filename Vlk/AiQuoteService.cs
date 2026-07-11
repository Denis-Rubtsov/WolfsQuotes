using OpenAI.Chat;

class AiQuoteService
{
    private readonly ChatClient _client;
    private readonly DataService _data;
    private readonly Random _random = new();

    public AiQuoteService(string apiKey, DataService data)
    {
        _client = new ChatClient("gpt-4o-mini", apiKey);
        _data = data;
    }

    public async Task<string> GenerateQuoteAsync()
    {
        List<string> examples;
        lock (_data.Lock)
        {
            examples = _data.Data.quotes
                .OrderBy(_ => _random.Next())
                .Take(5)
                .ToList();
        }

        const string systemPrompt =
            "Ты помогаешь придумывать короткие афористичные цитаты для телеграм-бота \"Волчьи цитаты\" " +
            "от лица мудрого и харизматичного Волка. Пиши на русском языке. Одна цитата, 1-2 предложения, " +
            "без кавычек и подписи, в философско-ироничном тоне. В ответе — только сама цитата.";

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
}
