using System.Text.Json;
using File = System.IO.File;

class DataService
{
    private readonly string _file;

    public readonly object Lock = new();
    public BotData Data { get; private set; }

    public DataService(string file)
    {
        _file = file;
        Data = File.Exists(file)
            ? JsonSerializer.Deserialize<BotData>(File.ReadAllText(file))!
            : new BotData();
    }

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
            if (Data.paid_generations.GetValueOrDefault(userId) <= 0)
                return false;

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
            if (exists(quote))
                return false;

            Data.quotes.Add(quote);
            Save();
            return true;
        }
    }

    public bool TryEditQuoteAt(int index, string newText)
    {
        lock (Lock)
        {
            if (index < 0 || index >= Data.quotes.Count)
                return false;

            Data.quotes[index] = newText;
            Save();
            return true;
        }
    }

    public string? TryRemoveQuoteAt(int index)
    {
        lock (Lock)
        {
            if (index < 0 || index >= Data.quotes.Count)
                return null;

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

            if (index < 0 || index >= list.Count)
                return null;

            var suggestion = list[index];

            if (approve && !exists(suggestion.quote))
                Data.quotes.Add(suggestion.quote);

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

    public void Save()
    {
        var json = JsonSerializer.Serialize(Data, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        // Пишем во временный файл и переименовываем, чтобы падение процесса
        // посреди записи не обрезало базу.
        var tmp = _file + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, _file, overwrite: true);
    }
}
