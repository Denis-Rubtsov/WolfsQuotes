class BotData
{
    public List<string> quotes { get; set; } = new();
    public List<Suggestion> suggestions { get; set; } = new();
    public Dictionary<string, QuoteRating> ratings { get; set; } = new();
    public bool allow_public_generation { get; set; }
    // Оплаченные звёздами генерации сверх бесплатного лимита: user id -> остаток.
    public Dictionary<long, int> paid_generations { get; set; } = new();
    // Цена одной такой генерации в звёздах; null — используется значение из конфига (StarsPrice).
    public int? stars_price { get; set; }
    // Время последнего Save(): по нему при старте выбирается более свежая копия — локальная или из S3.
    public DateTimeOffset? updated_at { get; set; }
}
