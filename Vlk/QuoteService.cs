using System.Security.Cryptography;
using System.Text;

class QuoteService
{
    private readonly DataService _data;
    private readonly Random _random = new();

    public QuoteService(DataService data)
    {
        _data = data;
    }

    public string? GetRandomQuote() =>
        _data.Read(d => d.quotes.Count == 0 ? null : d.quotes[_random.Next(d.quotes.Count)]);

    public bool Exists(string quote)
    {
        var normalized = quote.Trim().ToLower();
        return _data.Read(d => d.quotes.Any(q => q.Trim().ToLower() == normalized));
    }

    // Короткий стабильный ключ цитаты для callback-данных (лимит 64 байта) и словаря рейтингов.
    public string HashOf(string quote) =>
        Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes(quote.Trim().ToLower())))[..10];

    public (int Likes, int Dislikes)? Vote(string hash, long userId, bool like)
    {
        lock (_data.Lock)
        {
            if (!_data.Data.quotes.Any(q => HashOf(q) == hash))
                return null;

            if (!_data.Data.ratings.TryGetValue(hash, out var rating))
            {
                rating = new QuoteRating();
                _data.Data.ratings[hash] = rating;
            }

            var target = like ? rating.likes : rating.dislikes;
            var opposite = like ? rating.dislikes : rating.likes;

            opposite.Remove(userId);
            if (!target.Remove(userId))
                target.Add(userId);

            _data.Save();
            return (rating.likes.Count, rating.dislikes.Count);
        }
    }

    public (int Likes, int Dislikes) GetCounts(string hash) =>
        _data.Read(d => d.ratings.TryGetValue(hash, out var rating)
            ? (rating.likes.Count, rating.dislikes.Count)
            : (0, 0));
}
