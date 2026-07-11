class UserState
{
    public UserMode Mode { get; init; }
    public string? PendingQuote { get; set; }
    public int EditIndex { get; init; } = -1;
    public DateTime CreatedAtUtc { get; } = DateTime.UtcNow;

    public bool IsExpired(TimeSpan ttl) => DateTime.UtcNow - CreatedAtUtc > ttl;
}
