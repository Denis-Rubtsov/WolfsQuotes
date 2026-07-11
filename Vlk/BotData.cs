class BotData
{
    public List<string> quotes { get; set; } = new();
    public List<Suggestion> suggestions { get; set; } = new();
    public bool allow_public_generation { get; set; }
}
