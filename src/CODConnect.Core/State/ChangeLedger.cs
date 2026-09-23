using System.Text.Json;

namespace CODConnect.Core.State;

public sealed record ChangeRecord(
    DateTimeOffset Timestamp,
    string Kind,
    string Detail,
    string? Payload = null);

public sealed class ChangeLedger
{
    private readonly string _path;
    private readonly object _lock = new();

    public ChangeLedger(string? directory = null)
    {
        _path = Path.Combine(
            directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CODCONNECT"),
            "changes.jsonl");
    }

    public string FilePath => _path;

    public void Append(string kind, string detail, string? payload = null)
    {
        var record = new ChangeRecord(DateTimeOffset.UtcNow, kind, detail, payload);
        lock (_lock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.AppendAllText(_path, JsonSerializer.Serialize(record) + Environment.NewLine);
        }
    }

    public IReadOnlyList<ChangeRecord> ReadAll()
    {
        lock (_lock)
        {
            if (!File.Exists(_path))
            {
                return [];
            }

            var records = new List<ChangeRecord>();
            foreach (var line in File.ReadLines(_path))
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                try
                {
                    var record = JsonSerializer.Deserialize<ChangeRecord>(line);
                    if (record is not null)
                    {
                        records.Add(record);
                    }
                }
                catch (JsonException)
                {
                }
            }

            return records;
        }
    }
}
