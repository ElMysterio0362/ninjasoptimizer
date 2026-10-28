using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization.Actions;
using NinjaOptimizer.Core.Safety;

namespace NinjaOptimizer.Core.Optimization;

public sealed class ApplyOptions
{
    /// <summary>Cria um ponto de restauração do sistema antes de aplicar.</summary>
    public bool CreateRestorePoint { get; init; } = true;

    /// <summary>Se true, cancela tudo quando o ponto de restauração falha.</summary>
    public bool AbortIfRestorePointFails { get; init; }

    /// <summary>
    /// Chamado com os ajustes de risco alto. Sem confirmação explícita (ou sem callback),
    /// eles NUNCA são aplicados.
    /// </summary>
    public Func<IReadOnlyList<Tweak>, Task<bool>>? ConfirmHighRisk { get; init; }

    public IProgress<string>? Progress { get; init; }
}

public enum TweakOutcome { Applied, AlreadyApplied, Failed, SkippedHighRisk }

public sealed record TweakResult(Tweak Tweak, TweakOutcome Outcome, string Message, JournalEntry? Entry = null);

public sealed record ApplyReport(
    IReadOnlyList<TweakResult> Results,
    RestorePointResult? RestorePoint,
    bool Aborted = false,
    string? AbortReason = null)
{
    public int AppliedCount => Results.Count(r => r.Outcome == TweakOutcome.Applied);
    public int FailedCount => Results.Count(r => r.Outcome == TweakOutcome.Failed);
    public bool RequiresRestart => Results.Any(r => r.Outcome == TweakOutcome.Applied && r.Tweak.RequiresRestart);
}

public sealed record RevertResult(JournalEntry Entry, bool Success, string Message);

/// <summary>
/// Aplica e reverte otimizações com a sequência de segurança:
/// 1) bloqueia risco alto sem confirmação; 2) ponto de restauração; 3) backup .reg;
/// 4) snapshot do estado anterior; 5) aplicação atômica por ajuste; 6) journal.
/// </summary>
public sealed class OptimizationEngine(
    SystemContext ctx,
    ChangeJournal journal,
    IRestorePointService restorePoints,
    IRegistryBackup registryBackup,
    IElevationService elevation,
    IAppPaths paths,
    TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ChangeJournal Journal => journal;

    public async Task<bool> IsAppliedAsync(Tweak tweak, CancellationToken ct = default)
    {
        foreach (var a in tweak.Actions)
            if (!await a.IsAppliedAsync(ctx, ct)) return false;
        return true;
    }

    public async Task<ApplyReport> ApplyAsync(IEnumerable<Tweak> selection, ApplyOptions options, CancellationToken ct = default)
    {
        var tweaks = selection.DistinctBy(t => t.Id).ToList();
        var results = new List<TweakResult>();

        // 1) Risco alto exige confirmação explícita.
        var highRisk = tweaks.Where(t => t.Risk == RiskLevel.High).ToList();
        if (highRisk.Count > 0)
        {
            var confirmed = options.ConfirmHighRisk is not null && await options.ConfirmHighRisk(highRisk);
            if (!confirmed)
            {
                foreach (var t in highRisk)
                    results.Add(new(t, TweakOutcome.SkippedHighRisk, "Risco alto não confirmado pelo usuário."));
                tweaks = tweaks.Except(highRisk).ToList();
            }
        }

        // Ignora o que já está aplicado (nada a fazer, nada a registrar).
        var pending = new List<Tweak>();
        foreach (var t in tweaks)
        {
            if (await IsAppliedAsync(t, ct)) results.Add(new(t, TweakOutcome.AlreadyApplied, "Já estava aplicado."));
            else pending.Add(t);
        }

        if (pending.Count == 0) return new(results, null);

        if (pending.Any(t => t.RequiresAdmin) && !elevation.IsElevated)
            return new(results, null, Aborted: true,
                AbortReason: "Privilégios de administrador são necessários para os ajustes selecionados.");

        // 2) Ponto de restauração.
        RestorePointResult? rp = null;
        if (options.CreateRestorePoint)
        {
            options.Progress?.Report("Criando ponto de restauração do sistema…");
            rp = await restorePoints.CreateAsync($"NinjaOptimizer - antes de aplicar {pending.Count} ajuste(s)", ct);
            if (!rp.Success && options.AbortIfRestorePointFails)
                return new(results, rp, Aborted: true, AbortReason: "Falha ao criar ponto de restauração: " + rp.Message);
        }

        // 3) Backup .reg das chaves que serão alteradas.
        options.Progress?.Report("Exportando backup do registro…");
        var stamp = _time.GetLocalNow().ToString("yyyyMMdd-HHmmss");
        var backupDir = Path.Combine(paths.BackupFolder, stamp);
        var keys = pending.SelectMany(t => t.Actions).SelectMany(a => a.RegistryKeys).Distinct().ToList();
        IReadOnlyList<string> backups = keys.Count == 0 ? [] : await registryBackup.ExportAsync(keys, backupDir, ct);

        // 4–6) Aplica cada ajuste de forma atômica.
        foreach (var tweak in pending)
        {
            ct.ThrowIfCancellationRequested();
            options.Progress?.Report($"Aplicando: {tweak.Name}");
            results.Add(await ApplyOneAsync(tweak, rp?.Success == true, backups, ct));
        }

        return new(results, rp);
    }

    private async Task<TweakResult> ApplyOneAsync(Tweak tweak, bool restorePoint, IReadOnlyList<string> backups, CancellationToken ct)
    {
        var entry = new JournalEntry
        {
            TweakId = tweak.Id,
            TweakName = tweak.Name,
            AppliedAt = _time.GetLocalNow(),
            RestorePointCreated = restorePoint,
            RegistryBackups = backups.ToList(),
        };

        var applied = new List<ActionSnapshot>();
        try
        {
            // Captura TODO o estado anterior antes de mexer em qualquer coisa.
            var snapshots = new List<ActionSnapshot>();
            foreach (var action in tweak.Actions)
                snapshots.Add(await action.CaptureAsync(ctx, ct));

            for (var i = 0; i < tweak.Actions.Count; i++)
            {
                await tweak.Actions[i].ApplyAsync(ctx, snapshots[i], ct);
                applied.Add(snapshots[i]);
            }

            entry.Snapshots = snapshots;
            entry.Status = JournalStatus.Applied;
            journal.Add(entry);
            return new(tweak, TweakOutcome.Applied, "Aplicado com sucesso.", entry);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Desfaz as ações que já tinham sido aplicadas deste ajuste.
            var rollbackErrors = await RevertSnapshotsAsync(applied, ct);
            entry.Snapshots = applied;
            entry.Status = JournalStatus.Failed;
            entry.Error = ex.Message + (rollbackErrors.Count > 0 ? " | Rollback: " + string.Join("; ", rollbackErrors) : "");
            journal.Add(entry);
            return new(tweak, TweakOutcome.Failed, entry.Error, entry);
        }
    }

    /// <summary>Desfaz um item específico do histórico.</summary>
    public async Task<RevertResult> RevertAsync(Guid entryId, CancellationToken ct = default)
    {
        var entry = journal.Entries.FirstOrDefault(e => e.Id == entryId)
            ?? throw new ArgumentException("Entrada não encontrada no histórico.", nameof(entryId));
        if (entry.Status != JournalStatus.Applied)
            return new(entry, false, "Este item não está aplicado.");

        var errors = await RevertSnapshotsAsync(entry.Snapshots, ct);
        if (errors.Count == 0)
        {
            entry.Status = JournalStatus.Reverted;
            entry.RevertedAt = _time.GetLocalNow();
            entry.Error = null;
        }
        else
        {
            entry.Error = "Falha ao reverter: " + string.Join("; ", errors);
        }
        journal.Update(entry);
        return new(entry, errors.Count == 0, errors.Count == 0 ? "Revertido." : entry.Error!);
    }

    /// <summary>Reverte tudo, do mais recente para o mais antigo.</summary>
    public async Task<IReadOnlyList<RevertResult>> RevertAllAsync(CancellationToken ct = default)
    {
        var results = new List<RevertResult>();
        foreach (var e in journal.ActiveEntries)
            results.Add(await RevertAsync(e.Id, ct));
        return results;
    }

    public bool RevertRequiresAdmin(JournalEntry entry) =>
        entry.Snapshots.Any(s => s.Kind != RegistryValueAction.KindId || s.Get("hive") == nameof(RegHive.LocalMachine));

    private async Task<List<string>> RevertSnapshotsAsync(IEnumerable<ActionSnapshot> snapshots, CancellationToken ct)
    {
        var errors = new List<string>();
        foreach (var s in snapshots.Reverse())
        {
            try { await ActionReverter.RevertAsync(ctx, s, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException) { errors.Add($"{s.Description}: {ex.Message}"); }
        }
        return errors;
    }
}
