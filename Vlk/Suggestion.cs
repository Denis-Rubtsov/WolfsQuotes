class Suggestion
{
    public string id { get; set; } = Guid.NewGuid().ToString("N");
    public long user_id { get; set; }
    public string name { get; set; } = "";
    public string quote { get; set; } = "";
}
