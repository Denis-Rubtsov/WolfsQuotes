class QuoteService
{
    private readonly DataService _data;
    private readonly Random _random = new();

    public QuoteService(DataService data)
    {
        _data = data;
    }

    public int GetRandom()
    {
        lock (_data.Lock)
        {
            if (_data.Data.quotes.Count == 0)
                return 0;

            return _random.Next(_data.Data.quotes.Count);
        }
    }

    public bool Exists(string quote)
    {
        var normalized = quote.Trim().ToLower();

        lock (_data.Lock)
        {
            return _data.Data.quotes.Any(q => q.Trim().ToLower() == normalized);
        }
    }
}
