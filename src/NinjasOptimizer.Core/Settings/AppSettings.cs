using System.Text.Json;

namespace NinjasOptimizer.Core.Settings;

public sealed class AppSettings
{
    public bool CreateRestorePoint { get; set; } = true;
    public bool AbortIfRestorePointFails { get; set; }
    public int SensorIntervalMs { get; set; } = 1000;

    /// <summary>A fonte não é detectável por software; o usuário pode informar (usado na análise de gargalos).</summary>
    public string PowerSupply { get; set; } = "";
}

public sealed class SettingsStore(string path)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public AppSettings Load()
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Save(AppSettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(settings, Json));
    }
}
