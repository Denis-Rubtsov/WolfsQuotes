class BotData
{
    public List<string> quotes { get; set; } = new();
    public List<Suggestion> suggestions { get; set; } = new();
    public Dictionary<string, QuoteRating> ratings { get; set; } = new();
    public bool allow_public_generation { get; set; }
    // Оплаченные звёздами генерации сверх бесплатного лимита: user id -> остаток.
    public Dictionary<long, int> paid_generations { get; set; } = new();
}
