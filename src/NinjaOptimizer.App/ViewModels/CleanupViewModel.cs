using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Maintenance;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Windows.Platform;

namespace NinjaOptimizer.App.ViewModels;

public sealed partial class CleanupItemViewModel(MaintenanceTask task) : ObservableObject
{
    public MaintenanceTask Task { get; } = task;
    public string Name => Task.Name;
    public string Description => Task.Description;
    public string? Note => Task.Note;
    public bool HasNote => Task.Note is not null;
    public bool RequiresAdmin => Task.RequiresAdmin;
    public string ActionText => Task.ActionText;
    public string DurationText => "Leva " + Task.Duration;
    public bool IsMemory => Task.Category == TweakCategory.Memory;

    public string Icon => Task.Id switch
    {
        "cleanup.temp" => "Icon.File",
        "cleanup.recycle-bin" => "Icon.Trash",
        "cleanup.shader-cache" => "Icon.Gpu",
        "cleanup.windows-update" => "Icon.Download",
        "cleanup.components" => "Icon.Broom",
        "maintenance.optimize-drives" => "Icon.Disk",
        "memory.standby-list" => "Icon.Memory",
        "maintenance.dns-flush" => "Icon.Network",
        "maintenance.repair-system-files" => "Icon.Shield",
        "maintenance.restart-explorer" => "Icon.Refresh",
        "cleanup.recent-items" => "Icon.Clock",
        "maintenance.repair-windows-update" => "Icon.Download",
        "maintenance.repair-network" => "Icon.Network",
        "maintenance.check-disk" => "Icon.Disk",
        "maintenance.restart-audio" => "Icon.Refresh",
        "maintenance.restore-default-services" => "Icon.Settings",
        "maintenance.repair-store" => "Icon.Download",
        _ => "Icon.Settings",
    };

    [ObservableProperty] private long? _estimate;
    [ObservableProperty] private bool _isEstimating;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _lastRunText = "";

    public string EstimateText => IsEstimating ? "Analisando…"
        : Estimate is null ? (Task.Id.StartsWith("maintenance.") ? "" : "Tamanho só depois de rodar")
        : Estimate == 0 ? "Nada para limpar"
        : $"≈ {Format.Bytes((ulong)Estimate.Value)}{(IsMemory ? " em espera" : "")}";

    public StatusKind EstimateKind => Estimate is > 100 * 1024 * 1024 ? StatusKind.Accent : StatusKind.Neutral;

    partial void OnEstimateChanged(long? value) { OnPropertyChanged(nameof(EstimateText)); OnPropertyChanged(nameof(EstimateKind)); }
    partial void OnIsEstimatingChanged(bool value) => OnPropertyChanged(nameof(EstimateText));
}

/// <summary>Aba "Limpeza e manutenção": ações de fazer agora (sem desfazer), com estimativa antes e resultado depois.</summary>
public sealed partial class CleanupViewModel : ObservableObject
{
    private readonly DialogService _dialogs;
    private readonly IElevationService _elevation;
    private readonly MaintenanceStore _store;
    private readonly AppLog _log;
    private bool _analyzed;

    public ObservableCollection<CleanupItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _totalText = "";

    public CleanupViewModel(IReadOnlyList<MaintenanceTask> tasks, DialogService dialogs, IElevationService elevation, MaintenanceStore store, AppLog log)
    {
        _dialogs = dialogs;
        _elevation = elevation;
        _store = store;
        _log = log;
        foreach (var t in tasks) Items.Add(new CleanupItemViewModel(t));
        RefreshLastRuns();
    }

    /// <summary>Analisa o espaço uma vez, quando a aba é aberta.</summary>
    public Task EnsureAnalyzedAsync() => _analyzed ? System.Threading.Tasks.Task.CompletedTask : AnalyzeAsync();

    [RelayCommand]
    private async Task AnalyzeAsync()
    {
        _analyzed = true;
        TotalText = "Analisando seu PC…";
        await System.Threading.Tasks.Task.WhenAll(Items.Select(EstimateAsync));
        UpdateTotal();
    }

    private async Task EstimateAsync(CleanupItemViewModel item)
    {
        item.IsEstimating = true;
        try { item.Estimate = await item.Task.EstimateAsync(CancellationToken.None); }
        catch (Exception ex) { _log.Error($"Estimativa {item.Task.Id}", ex); item.Estimate = null; }
        finally { item.IsEstimating = false; }
    }

    private void UpdateTotal()
    {
        var disk = Items.Where(i => !i.IsMemory).Sum(i => i.Estimate ?? 0);
        TotalText = disk > 0 ? $"Dá para liberar cerca de {Format.Bytes((ulong)disk)} no disco." : "Nada relevante para limpar no disco agora.";
    }

    private void RefreshLastRuns()
    {
        var runs = _store.Load();
        foreach (var i in Items)
            i.LastRunText = runs.TryGetValue(i.Task.Id, out var r)
                ? $" · Última vez: {Format.When(r.At)}" + (r.BytesFreed > 0 ? $" · liberou {Format.Bytes((ulong)r.BytesFreed)}" : "")
                : "";
    }

    [RelayCommand]
    private async Task RunAsync(CleanupItemViewModel? item)
    {
        if (item is null || IsBusy) return;
        if (item.RequiresAdmin && !EnsureElevated()) return;

        var details = new List<string> { item.Description };
        if (item.Note is not null) details.Add("Atenção: " + item.Note);
        if (item.Task.Consequences.Count > 0)
        {
            details.Add("O que pode acontecer:");
            details.AddRange(item.Task.Consequences.Select(c => "   • " + c));
        }
        if (!_dialogs.Confirm($"{item.Name}?", $"Isso não tem como desfazer. Leva {item.Task.Duration}.", details,
                item.Note is null && item.Task.Consequences.Count == 0 ? DialogKind.Confirm : DialogKind.Danger, item.ActionText))
            return;

        IsBusy = true;
        item.IsRunning = true;
        try
        {
            var result = await item.Task.RunAsync(null, CancellationToken.None);
            _store.Record(item.Task.Id, new MaintenanceStore.Entry(DateTimeOffset.Now, result.BytesFreed, result.Success, result.Message));
            _log.Info($"Manutenção {item.Task.Id}: {result.Success} · {result.BytesFreed} bytes · {result.Message}");

            var where = item.IsMemory ? "de memória" : "no disco";
            var title = !result.Success ? "Não deu para concluir"
                : result.BytesFreed > 0 ? $"Pronto. Liberamos {Format.Bytes((ulong)result.BytesFreed)} {where}."
                : "Pronto.";
            var message = result.Message + (result.RequiresRestart && !result.Message.Contains("Reinicie") ? " Reinicie o PC para terminar." : "");
            if (result.FollowUp is { } next)
            {
                // Próximo passo opcional (ex.: corrigir o disco no próximo boot), sempre com confirmação.
                if (_dialogs.Confirm(next.Title, message, [next.Message], DialogKind.Confirm, next.ConfirmText))
                {
                    var r2 = await next.Run(CancellationToken.None);
                    _log.Info($"Manutenção {item.Task.Id} (passo seguinte): {r2.Success} · {r2.Message}");
                    _store.Record(item.Task.Id, new MaintenanceStore.Entry(DateTimeOffset.Now, 0, r2.Success, r2.Message));
                    _dialogs.Info(r2.Success ? "Pronto." : "Não deu para concluir", r2.Message, kind: r2.Success ? DialogKind.Success : DialogKind.Danger);
                }
            }
            else
            {
                _dialogs.Info(title, message, kind: result.Success ? DialogKind.Success : DialogKind.Danger);
            }
        }
        catch (Exception ex)
        {
            _log.Error($"Manutenção {item.Task.Id}", ex);
            _dialogs.Info("Não deu para concluir", ex.Message + " Nada além do que foi limpo até aqui mudou.", kind: DialogKind.Danger);
        }
        finally
        {
            item.IsRunning = false;
            IsBusy = false;
            RefreshLastRuns();
            await EstimateAsync(item);
            UpdateTotal();
        }
    }

    private bool EnsureElevated()
    {
        if (_elevation.IsElevated) return true;
        if (!_dialogs.Confirm("Abrir como administrador?",
                "Esta limpeza mexe em pastas do Windows. O NinjaOptimizer reabre como administrador nesta mesma aba.",
                confirmText: "Reabrir como administrador"))
            return false;
        if (WindowsElevationService.RelaunchElevated("--page cleanup")) Application.Current.Shutdown();
        return false;
    }
}
