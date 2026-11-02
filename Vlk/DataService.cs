using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using File = System.IO.File;

// База бота: один BotData в памяти, локальный JSON-файл и (если настроено) его копия в S3.
// Локальный файл пишется синхронно при каждом Save(), в S3 последняя версия уходит в фоне.
class DataService
{
    // Кириллица без \uXXXX-экранирования, чтобы базу можно было читать в веб-панели S3
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly TimeSpan UploadDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromMinutes(5);

    private readonly string? _file;
    private readonly S3Storage? _s3;
    private readonly string _key;
    private readonly int _backupDays;
    private readonly ILogger _logger;

    // Последняя ещё не отправленная в S3 версия; null — всё отправлено
    private string? _pending;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private readonly CancellationTokenSource _stop = new();
    private Task _uploader = Task.CompletedTask;
    private DateOnly? _backedUpOn;

    public readonly object Lock = new();
    public BotData Data { get; private set; } = new();

    private DataService(string? file, S3Storage? s3, string key, int backupDays, ILogger logger)
    {
        _file = string.IsNullOrWhiteSpace(file) ? null : file;
        _s3 = s3;
        _key = key;
        _backupDays = backupDays;
        _logger = logger;
    }

    public static async Task<DataService> LoadAsync(string? file, S3Storage? s3, string key, int backupDays, ILogger logger)
    {
        var service = new DataService(file, s3, key, backupDays, logger);
        if (service._file == null && s3 == null)
            throw new InvalidOperationException("Не задан ни QuotesFile, ни S3_BUCKET — негде хранить базу");

        await service.LoadAsync();
        return service;
    }

    private async Task LoadAsync()
    {
        var local = _file != null && File.Exists(_file)
            ? JsonSerializer.Deserialize<BotData>(await File.ReadAllTextAsync(_file))
            : null;

        if (_s3 == null)
        {
            Data = local ?? new BotData();
            return;
        }

        BotData? remote;
        try
        {
            var json = await _s3.GetTextAsync(_key);
            remote = json == null ? null : JsonSerializer.Deserialize<BotData>(json);
        }
        catch (Exception ex)
        {
            // Без S3 стартуем с локальной копии: бот один, так что она не старее той, что в S3.
            // Без обеих копий стартовать нельзя — первый же Save() затёр бы базу в S3 пустой.
            if (local == null)
                throw new InvalidOperationException($"S3 недоступен, а локальной копии базы ({_file ?? "QuotesFile не задан"}) нет", ex);

            _logger.LogWarning("S3 недоступен ({Error}), база загружена из локальной копии {File}", ex.Message, _file);
            Data = local;
            StartUploader();
            return;
        }

        if (remote != null && Stamp(remote) >= Stamp(local))
        {
            Data = remote;
            if (Stamp(remote) > Stamp(local)) WriteLocal(JsonSerializer.Serialize(Data, JsonOptions));
            _logger.LogInformation("База загружена из S3 ({Bucket}/{Key}), обновлена {UpdatedAt}", _s3.Bucket, _key, remote.updated_at);
        }
        else
        {
            Data = local ?? new BotData();
            if (remote == null) _logger.LogInformation("В S3 базы нет, отправляю туда локальную копию");
            else _logger.LogInformation("Локальная копия свежее, чем в S3, отправляю её");
            _pending = JsonSerializer.Serialize(Data, JsonOptions);
            _signal.Writer.TryWrite(true);
        }

        StartUploader();
        _ = WarnIfPublicAsync();
    }

    private static DateTimeOffset Stamp(BotData? data) =>
        data == null ? DateTimeOffset.MinValue : data.updated_at ?? DateTimeOffset.MinValue.AddTicks(1);

    public bool AllowPublicGeneration => Read(d => d.allow_public_generation);

    public int StarsPrice(int fallback) => Read(d => d.stars_price ?? fallback);

    public void SetStarsPrice(int price)
    {
        lock (Lock)
        {
            Data.stars_price = price;
            Save();
        }
    }

    public void AddPaidGenerations(long userId, int count)
    {
        lock (Lock)
        {
            Data.paid_generations[userId] = Data.paid_generations.GetValueOrDefault(userId) + count;
            Save();
        }
    }

    public bool TryConsumePaidGeneration(long userId)
    {
        lock (Lock)
        {
            if (Data.paid_generations.GetValueOrDefault(userId) <= 0) return false;

            Data.paid_generations[userId] -= 1;
            Save();
            return true;
        }
    }

    public bool TogglePublicGeneration()
    {
        lock (Lock)
        {
            Data.allow_public_generation = !Data.allow_public_generation;
            Save();
            return Data.allow_public_generation;
        }
    }

    public bool TryAddQuote(string quote, Func<string, bool> exists)
    {
        lock (Lock)
        {
            if (exists(quote)) return false;

            Data.quotes.Add(quote);
            Save();
            return true;
        }
    }

    public bool TryEditQuoteAt(int index, string newText)
    {
        lock (Lock)
        {
            if (index < 0 || index >= Data.quotes.Count) return false;

            Data.quotes[index] = newText;
            Save();
            return true;
        }
    }

    public string? TryRemoveQuoteAt(int index)
    {
        lock (Lock)
        {
            if (index < 0 || index >= Data.quotes.Count) return null;

            var removed = Data.quotes[index];
            Data.quotes.RemoveAt(index);
            Save();
            return removed;
        }
    }

    public void AddSuggestion(Suggestion suggestion)
    {
        lock (Lock)
        {
            Data.suggestions.Add(suggestion);
            Save();
        }
    }

    // Забирает предложение из очереди (по индексу или условию поиска);
    // при approve цитата попадает в базу, если её там ещё нет.
    public Suggestion? RemoveSuggestion(Func<List<Suggestion>, int> findIndex, bool approve, Func<string, bool> exists)
    {
        lock (Lock)
        {
            var list = Data.suggestions;
            var index = findIndex(list);

            if (index < 0 || index >= list.Count) return null;

            var suggestion = list[index];

            if (approve && !exists(suggestion.quote)) Data.quotes.Add(suggestion.quote);

            list.RemoveAt(index);
            Save();
            return suggestion;
        }
    }

    // Чтение/снимок данных под общим замком; мутации по-прежнему делаются
    // вручную под Lock с обязательным Save() до его освобождения.
    public T Read<T>(Func<BotData, T> read)
    {
        lock (Lock)
            return read(Data);
    }

    // Вызывать под Lock после каждой мутации
    public void Save()
    {
        Data.updated_at = DateTimeOffset.UtcNow;
        var json = JsonSerializer.Serialize(Data, JsonOptions);

        WriteLocal(json);

        if (_s3 == null) return;
        Volatile.Write(ref _pending, json);
        _signal.Writer.TryWrite(true);
    }

    private void WriteLocal(string json)
    {
        if (_file == null) return;

        // Пишем во временный файл и переименовываем, чтобы падение процесса
        // посреди записи не обрезало базу.
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _file, overwrite: true);
    }

    private void StartUploader() => _uploader = Task.Run(UploadLoopAsync);

    // Ждёт изменений, выжидает UploadDelay (пачка Save() подряд — один PUT) и отправляет последнюю версию;
    // при ошибке повторяет с растущей паузой.
    private async Task UploadLoopAsync()
    {
        var retryDelay = UploadDelay;
        try
        {
            while (await _signal.Reader.WaitToReadAsync(_stop.Token))
            {
                _signal.Reader.TryRead(out _);
                await Task.Delay(UploadDelay, _stop.Token);

                while (!await TryUploadAsync(_stop.Token))
                {
                    await Task.Delay(retryDelay, _stop.Token);
                    retryDelay = TimeSpan.FromTicks(Math.Min(retryDelay.Ticks * 2, MaxRetryDelay.Ticks));
                }

                retryDelay = UploadDelay;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // true — отправлять нечего или отправка удалась
    private async Task<bool> TryUploadAsync(CancellationToken ct)
    {
        var json = Interlocked.Exchange(ref _pending, null);
        if (json == null) return true;

        try
        {
            await _s3!.PutTextAsync(_key, json, "application/json", ct);
        }
        catch (Exception ex)
        {
            // Вернуть версию в очередь, если за это время не появилась более новая
            Interlocked.CompareExchange(ref _pending, json, null);
            if (ex is OperationCanceledException && ct.IsCancellationRequested) throw;
            _logger.LogWarning("Не удалось отправить базу в S3 ({Error}), повторю позже; локальная копия сохранена", ex.Message);
            return false;
        }

        await BackupAsync(ct);
        return true;
    }

    // Раз в сутки — копия базы в backups/<ключ>-ГГГГ-ММ-ДД.json, старше _backupDays удаляются.
    private async Task BackupAsync(CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (_backupDays <= 0 || _backedUpOn == today) return;

        var prefix = "backups/" + Path.GetFileNameWithoutExtension(_key) + "-";
        try
        {
            var backupKey = $"{prefix}{today:yyyy-MM-dd}.json";
            if (!await _s3!.ExistsAsync(backupKey, ct))
            {
                await _s3.CopyAsync(_key, backupKey, ct);
                _logger.LogInformation("Бэкап базы в S3: {Key}", backupKey);
            }

            // Ключи с датой сортируются по времени; оставляем _backupDays последних
            var old = (await _s3.ListKeysAsync(prefix, ct)).Order(StringComparer.Ordinal).SkipLast(_backupDays);
            foreach (var key in old)
                await _s3.DeleteAsync(key, ct);

            _backedUpOn = today;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning("Не удалось сделать бэкап базы в S3 ({Error}), попробую при следующей отправке", ex.Message);
        }
    }

    // При остановке: дождаться фоновой отправки и отправить последнюю версию, не дольше timeout.
    public async Task FlushAsync(TimeSpan timeout)
    {
        if (_s3 == null) return;

        _stop.Cancel();
        await _uploader;

        using var deadline = new CancellationTokenSource(timeout);
        try
        {
            while (!await TryUploadAsync(deadline.Token))
                await Task.Delay(TimeSpan.FromSeconds(1), deadline.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogError("Не успел отправить последнюю версию базы в S3 при остановке; она есть в локальной копии {File}", _file);
        }
    }

    private async Task WarnIfPublicAsync()
    {
        try
        {
            if (await _s3!.IsPubliclyReadableAsync(_key))
                _logger.LogError("⚠️ Базу {Bucket}/{Key} можно скачать без ключей — бакет публичный! Там Telegram ID и имена пользователей. Сделай бакет приватным", _s3.Bucket, _key);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Не удалось проверить публичность бакета");
        }
    }
}
