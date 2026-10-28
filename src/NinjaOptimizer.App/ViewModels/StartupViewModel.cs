using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Startup;
using NinjaOptimizer.Windows.Platform;

namespace NinjaOptimizer.App.ViewModels;

public sealed partial class StartupItemViewModel(StartupEntry entry, StartupViewModel owner) : ObservableObject
{
    public StartupEntry Entry { get; private set; } = entry;
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Entry.Name) is { Length: > 0 } n && Entry.Source is StartupSource.UserFolder or StartupSource.MachineFolder ? n : Entry.Name;
    public string Program => StartupManager.ExecutableOf(Entry.Command);
    public string Command => Entry.Command;
    public string ScopeText => Entry.AllUsers ? "Todos os usuários" : "Só a sua conta";
    public string StateText => Entry.Enabled ? "Abre com o Windows" : "Desativado";
    public StatusKind StateKind => Entry.Enabled ? StatusKind.Accent : StatusKind.Neutral;

    public bool IsEnabled
    {
        get => Entry.Enabled;
        set
        {
            if (value == Entry.Enabled) return;
            if (owner.Toggle(this, value)) Entry = Entry with { Enabled = value };
            OnPropertyChanged(string.Empty);
        }
    }
}

/// <summary>Aba "Inicialização": liga/desliga programas que abrem com o Windows (como o Gerenciador de Tarefas).</summary>
public sealed partial class StartupViewModel(StartupManager manager, DialogService dialogs, IElevationService elevation, AppLog log) : ObservableObject
{
    public ObservableCollection<StartupItemViewModel> Items { get; } = new();
    [ObservableProperty] private string _summary = "";

    [RelayCommand]
    public void Refresh()
    {
        Items.Clear();
        try
        {
            foreach (var e in manager.List()) Items.Add(new StartupItemViewModel(e, this));
        }
        catch (Exception ex) { log.Error("Listar inicialização", ex); }
        UpdateSummary();
    }

    private void UpdateSummary()
    {
        var on = Items.Count(i => i.IsEnabled);
        Summary = Items.Count == 0 ? "Nenhum programa abre com o Windows."
            : $"{on} de {Items.Count} programa(s) abrem com o Windows";
    }

    internal bool Toggle(StartupItemViewModel item, bool enable)
    {
        if (item.Entry.AllUsers && !elevation.IsElevated)
        {
            if (dialogs.Confirm("Abrir como administrador?",
                    "Este programa abre para todos os usuários do PC, então mudar exige administrador. O NinjaOptimizer reabre nesta mesma aba.",
                    confirmText: "Reabrir como administrador")
                && WindowsElevationService.RelaunchElevated("--page startup"))
                Application.Current.Shutdown();
            return false;
        }
        try
        {
            manager.SetEnabled(item.Entry, enable);
            log.Info($"Inicialização {item.Entry.Id}: {(enable ? "ativado" : "desativado")}");
            Application.Current.Dispatcher.BeginInvoke(UpdateSummary);
            return true;
        }
        catch (Exception ex)
        {
            log.Error("Alterar inicialização", ex);
            dialogs.Info("Não deu para mudar", ex.Message, kind: DialogKind.Danger);
            return false;
        }
    }
}
