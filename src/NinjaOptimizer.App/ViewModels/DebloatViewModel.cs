using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Debloat;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Settings;

namespace NinjaOptimizer.App.ViewModels;

public sealed partial class AppItemViewModel : ObservableObject
{
    public InstalledApp? App { get; }
    public bool IsOneDrive { get; }
    public AppVerdict Verdict { get; }
    public string Name { get; }
    public string PackageName { get; }
    public IReadOnlyList<string> Consequences { get; }

    public AppItemViewModel(InstalledApp? app, bool oneDrive = false)
    {
        App = app;
        IsOneDrive = oneDrive;
        var entry = app is null ? null : AppCatalog.Find(app.Name);
        Verdict = oneDrive ? AppVerdict.Caution : entry?.Verdict ?? AppVerdict.Unknown;
        Name = oneDrive ? "OneDrive" : entry?.FriendlyName ?? app!.Name;
        PackageName = oneDrive ? "Microsoft.OneDrive (programa, não é app da Store)" : app!.Name;
        Consequences = oneDrive ? DebloatTweaks.RemoveOneDrive().Consequences.Take(3).ToList() : entry?.Consequences ?? AppCatalog.UnknownConsequences;
    }

    public bool IsKnown => Verdict != AppVerdict.Unknown;
    public string VerdictText => Verdict switch
    {
        AppVerdict.Safe => "Seguro remover",
        AppVerdict.Caution => "Cuidado",
        AppVerdict.NotRecommended => "Não recomendado",
        _ => "Desconhecido",
    };
    public StatusKind VerdictKind => Verdict switch
    {
        AppVerdict.Safe => StatusKind.Ok,
        AppVerdict.Caution => StatusKind.Warning,
        _ => StatusKind.Danger,
    };

    /// <summary>Não recomendado e desconhecido exigem "Entendi as consequências".</summary>
    public bool NeedsAcknowledge => Verdict is AppVerdict.NotRecommended or AppVerdict.Unknown;
    [ObservableProperty] private bool _isAcknowledged;
    [ObservableProperty] private bool _isSelected;
    public bool CanSelect => !NeedsAcknowledge || IsAcknowledged;
    partial void OnIsAcknowledgedChanged(bool value) { if (!value) IsSelected = false; OnPropertyChanged(nameof(CanSelect)); }
    partial void OnIsSelectedChanged(bool value) { if (value && !CanSelect) IsSelected = false; }

    public Tweak ToTweak(bool alsoProvisioned) => IsOneDrive ? DebloatTweaks.RemoveOneDrive() : DebloatTweaks.Remove(App!, alsoProvisioned);
}

/// <summary>Aba "Apps do Windows": remover apps escolhidos, com consequências antes e Desfazer (reinstala) depois.</summary>
public sealed partial class DebloatViewModel(OptimizationEngine engine, ICommandRunner commands, DialogService dialogs,
    IElevationService elevation, AppSettings settings, AppLog log) : ObservableObject
{
    private List<AppItemViewModel> _all = [];
    public ObservableCollection<AppItemViewModel> Items { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private bool _showUnknown;
    [ObservableProperty] private bool _alsoForNewAccounts;
    private bool _loaded;

    /// <summary>Chamado depois de remover ou desfazer (a tela Otimizar atualiza o histórico).</summary>
    public Func<Task>? Changed { get; set; }

    partial void OnShowUnknownChanged(bool value) => Filter();

    public Task EnsureLoadedAsync() => _loaded ? Task.CompletedTask : RefreshAsync();

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Summary = "Procurando apps instalados…";
        try
        {
            var apps = Appx.Removable(await Task.Run(() => Appx.ListAsync(commands, CancellationToken.None)));
            var list = apps.Select(a => new AppItemViewModel(a)).ToList();
            if (await Task.Run(() => OneDriveRemoveAction.IsInstalledAsync(commands, CancellationToken.None)))
                list.Add(new AppItemViewModel(null, oneDrive: true));
            _all = [.. list.OrderBy(i => i.Verdict).ThenBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase)];
            _loaded = true;
        }
        catch (Exception ex)
        {
            log.Error("Listar apps", ex);
            Summary = "Não foi possível listar os apps: " + ex.Message;
            _all = [];
        }
        finally { IsBusy = false; }
        Filter();
    }

    private void Filter()
    {
        Items.Clear();
        foreach (var i in _all.Where(i => i.IsKnown || ShowUnknown)) Items.Add(i);
        var unknown = _all.Count(i => !i.IsKnown);
        if (_loaded)
            Summary = $"{_all.Count(i => i.IsKnown)} app(s) conhecido(s) instalado(s)" + (unknown > 0 ? $" · {unknown} outro(s)" : "") + ". Nada vem marcado.";
    }

    [RelayCommand]
    private async Task RemoveSelectedAsync()
    {
        var chosen = _all.Where(i => i.IsSelected).ToList();
        if (chosen.Count == 0) { dialogs.Info("Nada selecionado", "Marque os apps que você quer remover."); return; }
        var tweaks = chosen.Select(c => c.ToTweak(AlsoForNewAccounts && !c.IsOneDrive)).ToList();
        if (tweaks.Any(t => t.RequiresAdmin) && !elevation.IsElevated)
        {
            dialogs.Info("Precisa de administrador", "\"Remover também para contas novas\" só funciona com o NinjaOptimizer aberto como administrador. Desmarque a opção ou reabra como administrador em Configurações.");
            return;
        }

        // Resumo final com todas as consequências juntas.
        var details = new List<string>();
        foreach (var t in tweaks)
        {
            details.Add(t.Name.Replace("Remover app: ", ""));
            details.AddRange(t.Consequences.Select(c => "   • " + c));
        }
        if (!dialogs.Confirm($"Remover {tweaks.Count} app(s)?", "Leia o que acontece com cada um. Dá para reinstalar depois pelo Desfazer do histórico.",
                details, DialogKind.Danger, "Remover"))
            return;

        IsBusy = true;
        try
        {
            var report = await Task.Run(() => engine.ApplyAsync(tweaks, new ApplyOptions
            {
                CreateRestorePoint = settings.CreateRestorePoint,
                AbortIfRestorePointFails = settings.AbortIfRestorePointFails,
                ConfirmHighRisk = _ => Task.FromResult(true), // o resumo acima já foi a confirmação
            }));
            log.Info($"Debloat: {report.AppliedCount} removido(s), {report.FailedCount} falha(s)");
            var failed = report.Results.Where(r => r.Outcome == TweakOutcome.Failed).Select(r => $"{r.Tweak.Name}: {r.Message}").ToList();
            dialogs.Info(failed.Count == 0 ? $"{report.AppliedCount} app(s) removido(s)." : "Alguns apps não foram removidos",
                "Para reinstalar, use Desfazer no histórico de alterações (aba Ajustes).", failed,
                failed.Count == 0 ? DialogKind.Success : DialogKind.Danger);
        }
        catch (Exception ex)
        {
            log.Error("Debloat", ex);
            dialogs.Info("Não deu para remover", ex.Message, kind: DialogKind.Danger);
        }
        finally { IsBusy = false; }
        if (Changed is { } c) await c();
        await RefreshAsync();
    }
}
