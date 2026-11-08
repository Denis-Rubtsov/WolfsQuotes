using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

// Telegram не всегда может сам скачать озвучку по ссылке (так вышло с selstorage.ru),
// поэтому бот скачивает файл сам, один раз загружает его в Telegram и дальше шлёт по file_id.
// file_id живут только в памяти: после перезапуска каждый файл загрузится заново при первом запросе.
class VoiceCache
{
    private readonly string _voiceUrl;
    private readonly long _uploadChatId;
    private readonly ILogger<VoiceCache> _logger;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };
    private readonly ConcurrentDictionary<int, Lazy<Task<string?>>> _fileIds = new();

    public VoiceCache(string voiceUrl, long uploadChatId, ILogger<VoiceCache> logger)
    {
        _voiceUrl = voiceUrl;
        _uploadChatId = uploadChatId;
        _logger = logger;
    }

    public bool Enabled => !string.IsNullOrEmpty(_voiceUrl);

    public string UrlOf(int index) => _voiceUrl + $"{index}.ogg";

    // null — загрузить не удалось (файла нет, хранилище или Telegram недоступны); повторим при следующем запросе
    public async Task<string?> GetFileIdAsync(ITelegramBotClient bot, int index)
    {
        if (string.IsNullOrEmpty(_voiceUrl) || _uploadChatId == 0) return null;

        var lazy = _fileIds.GetOrAdd(index, i => new Lazy<Task<string?>>(() => UploadAsync(bot, i)));
        var fileId = await lazy.Value;
        if (fileId == null) _fileIds.TryRemove(new KeyValuePair<int, Lazy<Task<string?>>>(index, lazy));
        return fileId;
    }

    private async Task<string?> UploadAsync(ITelegramBotClient bot, int index)
    {
        try
        {
            await using var stream = await _http.GetStreamAsync(UrlOf(index));
            // Чтобы получить file_id, файл надо куда-то отправить: шлём первому админу без звука и сразу удаляем
            var message = await bot.SendVoice(_uploadChatId, InputFile.FromStream(stream, $"{index}.ogg"), disableNotification: true);
            try { await bot.DeleteMessage(_uploadChatId, message.MessageId); }
            catch (Exception ex) { _logger.LogWarning("Не удалось удалить служебное голосовое ({Error})", ex.Message); }

            _logger.LogInformation("Озвучка {Index}.ogg загружена в Telegram", index);
            return message.Voice?.FileId;
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Не удалось загрузить озвучку {Index}.ogg в Telegram ({Error})", index, ex.Message);
            return null;
        }
    }
}
