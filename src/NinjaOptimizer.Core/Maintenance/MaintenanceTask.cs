using System.Text.Json;
using NinjaOptimizer.Core.Optimization;

namespace NinjaOptimizer.Core.Maintenance;

public sealed record MaintenanceResult(bool Success, long BytesFreed, string Message)
{
    /// <summary>Próximo passo opcional que o app oferece com confirmação (ex.: corrigir o disco no próximo boot).</summary>
    public MaintenanceFollowUp? FollowUp { get; init; }
    /// <summary>O Windows precisa reiniciar para terminar.</summary>
    public bool RequiresRestart { get; init; }
}

public sealed record MaintenanceFollowUp(string Title, string Message, string ConfirmText, Func<CancellationToken, Task<MaintenanceResult>> Run);

/// <summary>
/// Ação de limpeza ou manutenção executada na hora ("Limpar agora"). Diferente dos ajustes, não tem
/// estado para reverter: por isso cada uma diz claramente o que apaga antes de rodar.
/// </summary>
public abstract class MaintenanceTask
{
    public abstract string Id { get; }
    public abstract string Name { get; }
    public abstract string Description { get; }
    public abstract TweakCategory Category { get; }

    /// <summary>Aviso exibido antes de executar (o que pode acontecer depois).</summary>
    public virtual string? Note => null;
    public virtual bool RequiresAdmin => false;
    /// <summary>Texto curto de duração esperada ("segundos", "alguns minutos").</summary>
    public virtual string Duration => "segundos";
    public virtual string ActionText => "Limpar agora";

    /// <summary>Quanto deve liberar, em bytes (null = não dá para estimar).</summary>
    public virtual Task<long?> EstimateAsync(CancellationToken ct) => Task.FromResult<long?>(null);

    public abstract Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct);
}

/// <summary>Apaga arquivos de pastas (e subpastas), pulando os que estão em uso.</summary>
public static class FolderCleaner
{
    public sealed record Summary(long Bytes, int Files, int Skipped);

    private static IEnumerable<FileInfo> Files(IEnumerable<string> folders, TimeSpan minAge, DateTime now)
    {
        foreach (var folder in folders.Where(Directory.Exists))
        {
            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions
                {
                    RecurseSubdirectories = true,
                    IgnoreInaccessible = true,
                    AttributesToSkip = FileAttributes.ReparsePoint, // nunca segue atalhos de pasta para fora
                });
            }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var f in files)
            {
                DateTime last;
                try { last = f.LastWriteTimeUtc > f.LastAccessTimeUtc ? f.LastWriteTimeUtc : f.LastAccessTimeUtc; }
                catch (IOException) { continue; }
                if (now - last >= minAge) yield return f;
            }
        }
    }

    public static Summary Measure(IEnumerable<string> folders, TimeSpan minAge, DateTime? now = null)
    {
        long bytes = 0;
        var count = 0;
        foreach (var f in Files(folders, minAge, now ?? DateTime.UtcNow))
        {
            try { bytes += f.Length; count++; } catch (IOException) { }
        }
        return new Summary(bytes, count, 0);
    }

    public static Summary Clean(IEnumerable<string> folders, TimeSpan minAge, CancellationToken ct, DateTime? now = null)
    {
        var list = folders.ToList();
        long bytes = 0;
        int count = 0, skipped = 0;
        foreach (var f in Files(list, minAge, now ?? DateTime.UtcNow).ToList())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var len = f.Length;
                if (f.IsReadOnly) f.IsReadOnly = false;
                f.Delete();
                bytes += len;
                count++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                skipped++; // em uso ou protegido: fica para a próxima
            }
        }
        // Remove subpastas que ficaram vazias (nunca a pasta raiz).
        foreach (var root in list.Where(Directory.Exists))
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
                {
                    try { if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return new Summary(bytes, count, skipped);
    }
}

/// <summary>Limpeza de um conjunto de pastas (temporários, caches).</summary>
public class FolderCleanupTask(
    string id, string name, string description, TweakCategory category,
    Func<IEnumerable<string>> folders, TimeSpan minAge, string? note = null, bool requiresAdmin = false) : MaintenanceTask
{
    public override string Id => id;
    public override string Name => name;
    public override string Description => description;
    public override TweakCategory Category => category;
    public override string? Note => note;
    public override bool RequiresAdmin => requiresAdmin;

    public override Task<long?> EstimateAsync(CancellationToken ct) =>
        Task.Run(() => (long?)FolderCleaner.Measure(folders(), minAge).Bytes, ct);

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        progress?.Report($"Limpando: {Name}");
        var s = FolderCleaner.Clean(folders(), minAge, ct);
        var skipped = s.Skipped > 0 ? $" {s.Skipped} arquivo(s) em uso ficaram para a próxima." : "";
        return new MaintenanceResult(true, s.Bytes, $"{s.Files} arquivo(s) apagado(s).{skipped}");
    }, ct);
}

/// <summary>Última execução de cada tarefa (maintenance.json).</summary>
public sealed class MaintenanceStore(string path)
{
    public sealed record Entry(DateTimeOffset At, long BytesFreed, bool Success, string Message);

    public Dictionary<string, Entry> Load()
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void Record(string taskId, Entry entry)
    {
        var all = Load();
        all[taskId] = entry;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(all));
    }
}
