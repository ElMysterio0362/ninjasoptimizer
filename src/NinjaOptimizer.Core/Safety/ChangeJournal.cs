using System.Text.Json;
using System.Text.Json.Serialization;
using NinjaOptimizer.Core.Optimization.Actions;

namespace NinjaOptimizer.Core.Safety;

public enum JournalStatus { Applied, Reverted, Failed }

/// <summary>Registro de uma otimização aplicada, com tudo o que é preciso para desfazê-la.</summary>
public sealed class JournalEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TweakId { get; set; } = "";
    public string TweakName { get; set; } = "";
    public DateTimeOffset AppliedAt { get; set; }
    public DateTimeOffset? RevertedAt { get; set; }
    public JournalStatus Status { get; set; }
    public string? Error { get; set; }
    public bool RestorePointCreated { get; set; }
    public List<string> RegistryBackups { get; set; } = new();
    public List<ActionSnapshot> Snapshots { get; set; } = new();
}

/// <summary>Journal persistido em JSON. Escrita atômica (arquivo temporário + replace).</summary>
public sealed class ChangeJournal
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;
    private readonly object _gate = new();
    private List<JournalEntry> _entries;

    public ChangeJournal(string path)
    {
        _path = path;
        _entries = Load(path);
    }

    public IReadOnlyList<JournalEntry> Entries
    {
        get { lock (_gate) return _entries.ToList(); }
    }

    /// <summary>Entradas aplicadas e ainda não revertidas, da mais recente para a mais antiga.</summary>
    public IReadOnlyList<JournalEntry> ActiveEntries
    {
        get
        {
            lock (_gate)
                return _entries.Where(e => e.Status == JournalStatus.Applied)
                    .OrderByDescending(e => e.AppliedAt).ToList();
        }
    }

    public void Add(JournalEntry entry)
    {
        lock (_gate)
        {
            _entries.Add(entry);
            Save();
        }
    }

    public void Update(JournalEntry entry)
    {
        lock (_gate)
        {
            var i = _entries.FindIndex(e => e.Id == entry.Id);
            if (i >= 0) _entries[i] = entry;
            Save();
        }
    }

    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_entries, Json));
        File.Move(tmp, _path, overwrite: true);
    }

    private static List<JournalEntry> Load(string path)
    {
        if (!File.Exists(path)) return new();
        try
        {
            return JsonSerializer.Deserialize<List<JournalEntry>>(File.ReadAllText(path), Json) ?? new();
        }
        catch (JsonException)
        {
            // Journal corrompido: preserva uma cópia para análise e começa um novo.
            File.Copy(path, path + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}", overwrite: true);
            return new();
        }
    }
}
