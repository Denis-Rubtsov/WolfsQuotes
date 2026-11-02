using System.Runtime.InteropServices;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

class Program
{
    static async Task Main()
    {
        var config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", true)
            .AddEnvironmentVariables()
            .Build();

        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.AddConfiguration(config.GetSection("Logging"));
            builder.AddConsole();
        });

        var quotesFile = config["QuotesFile"];
        var token = config["Token"];
        var adminIds = (config["ADMIN_IDS"] ?? "0")
            .Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(id => long.Parse(id.Trim()))
            .ToList();
        var voice = config["BASIC_URL"] ?? "";
        var aiApiKey = config["AiApikey"];
        var promptFile = config["SystemPromptFile"];

        var log = loggerFactory.CreateLogger<Program>();

        // Приватный бакет для базы и промпта; адрес сервера — тот же, что у публичного бакета с озвучкой (BASIC_URL)
        S3Storage? s3 = null;
        if (!string.IsNullOrWhiteSpace(config["S3_BUCKET"]))
        {
            var serviceUrl = config["S3_URL"] ?? S3Storage.ServiceUrlFrom(voice)
                ?? throw new InvalidOperationException("Не удалось взять адрес S3 из BASIC_URL — задай S3_URL");
            s3 = new S3Storage(serviceUrl, config["S3_BUCKET"]!, config["S3_REGION"] ?? "us-east-1",
                config["S3_ACCESS_KEY"] ?? throw new InvalidOperationException("Не задан S3_ACCESS_KEY"),
                config["S3_SECRET_KEY"] ?? throw new InvalidOperationException("Не задан S3_SECRET_KEY"));
            log.LogInformation("База и промпт хранятся в S3: {Url}/{Bucket}", serviceUrl, s3.Bucket);
        }

        var backupDays = int.TryParse(config["S3_BACKUP_DAYS"], out var days) ? days : 30;

        var bot = new TelegramBotClient(token);

        var data = await DataService.LoadAsync(quotesFile, s3, config["S3_QUOTES_KEY"] ?? "quotes.json", backupDays,
            loggerFactory.CreateLogger<DataService>());
        var quotes = new QuoteService(data);
        var ai = new AiQuoteService(aiApiKey, promptFile, s3, config["S3_PROMPT_KEY"] ?? "systemprompt.txt", data,
            loggerFactory.CreateLogger<AiQuoteService>());

        // Лимит ИИ-генераций для обычных пользователей; на админов не действует.
        var rateLimiter = new RateLimiter(5, TimeSpan.FromHours(1));

        // Цена одной генерации сверх лимита в звёздах Telegram; 0 отключает продажу.
        var starsPrice = int.TryParse(config["StarsPrice"], out var price) ? price : 10;

        var inline = new InlineHandler(data, ai, adminIds, voice, rateLimiter, loggerFactory.CreateLogger<InlineHandler>());

        var service = new BotService(bot, inline, data, quotes, ai, adminIds, rateLimiter, starsPrice, loggerFactory.CreateLogger<BotService>());

        // SIGTERM (podman stop / systemctl restart) и Ctrl+C: перестать принимать обновления и дослать базу в S3
        using var stopping = new CancellationTokenSource();
        using var sigterm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, ctx => { ctx.Cancel = true; stopping.Cancel(); });
        using var sigint = PosixSignalRegistration.Create(PosixSignal.SIGINT, ctx => { ctx.Cancel = true; stopping.Cancel(); });

        service.Start(stopping.Token);

        log.LogInformation("Бот запущен");
        try { await Task.Delay(Timeout.Infinite, stopping.Token); }
        catch (OperationCanceledException) { }

        log.LogInformation("Остановка: отправляю базу в S3");
        await Task.Delay(TimeSpan.FromSeconds(1)); // дать доработать уже начатым обработчикам
        await data.FlushAsync(TimeSpan.FromSeconds(20));
        log.LogInformation("Бот остановлен");
    }
}
