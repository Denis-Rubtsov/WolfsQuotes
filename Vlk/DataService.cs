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

        if (File.Exists(file))
            Data = JsonSerializer.Deserialize<BotData>(File.ReadAllText(file))!;
        else
            Data = new BotData();
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
