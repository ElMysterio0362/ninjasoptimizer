using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NinjaOptimizer.App.Services;
using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Network;
using NinjaOptimizer.Core.Optimization;
using NinjaOptimizer.Core.Settings;
using NinjaOptimizer.Windows.Platform;

namespace NinjaOptimizer.App.ViewModels;

public sealed partial class DnsProviderItem(DnsProvider provider) : ObservableObject
{
    public DnsProvider Provider { get; } = provider;
    public string Name => Provider.Name;
    public string Note => Provider.Note;
    public string ServersText => Provider.IsAutomatic ? "Do roteador" : string.Join(" · ", Provider.Servers);

    [ObservableProperty] private bool _isCurrent;
    [ObservableProperty] private bool _isFastest;
    [ObservableProperty] private string _latencyText = "";
    [ObservableProperty] private StatusKind _latencyKind = StatusKind.Neutral;
}

/// <summary>Aba "DNS": escolha do provedor (cada um é um ajuste com Desfazer) e teste de velocidade por UDP direto.</summary>
public sealed partial class DnsViewModel(OptimizationEngine engine, DialogService dialogs, IElevationService elevation, AppSettings settings, AppLog log)
    : ObservableObject
{
    public ObservableCollection<DnsProviderItem> Providers { get; } = new(DnsProviders.All.Select(p => new DnsProviderItem(p)));

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Teste a velocidade para ver qual responde mais rápido na sua internet.";

    /// <summary>Chamado depois de trocar o DNS (a tela Otimizar atualiza o histórico).</summary>
    public Func<Task>? Changed { get; set; }

    public async Task RefreshAsync()
    {
        foreach (var p in Providers)
        {
            try { p.IsCurrent = await Task.Run(() => engine.IsAppliedAsync(TweakCatalog.Get(p.Provider.TweakId))); }
            catch (Exception ex) { log.Error("Estado do DNS", ex); }
        }
    }

    /// <summary>DNS do roteador para o teste: o DNS atual (se automático) ou o gateway.</summary>
    private IPAddress? RouterDns()
    {
        var auto = Providers.FirstOrDefault(p => p.Provider.IsAutomatic)?.IsCurrent == true;
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback))
        {
            var ip = nic.GetIPProperties();
            if (ip.GatewayAddresses.Count == 0) continue;
            if (auto && ip.DnsAddresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } dns) return dns;
            if (ip.GatewayAddresses.Select(g => g.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork) is { } gw) return gw;
        }
        return null;
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        if (IsBusy) return;
        IsBusy = true;
        Status = "Testando… (alguns segundos)";
        foreach (var p in Providers) { p.LatencyText = "…"; p.IsFastest = false; p.LatencyKind = StatusKind.Neutral; }
        try
        {
            var router = RouterDns();
            var results = await Task.Run(() => new DnsSpeedTest(new UdpDnsQuery()).RunAsync(DnsProviders.All, router, CancellationToken.None));
            var fastest = DnsSpeedTest.Fastest(results);
            foreach (var r in results)
            {
                var item = Providers.First(p => p.Provider == r.Provider);
                item.LatencyText = r.Asked == 0 ? "Sem roteador" : r.MedianMs is { } ms ? $"{ms:0} ms" + (r.Answered < r.Asked ? $" · {r.Asked - r.Answered} sem resposta" : "") : "Sem resposta";
                item.IsFastest = r == fastest;
                item.LatencyKind = item.IsFastest ? StatusKind.Accent : r.MedianMs is null ? StatusKind.Warning : StatusKind.Neutral;
            }
            Status = fastest is null ? "Nenhum DNS respondeu. Confira a internet." : $"Mais rápido aqui: {fastest.Provider.Name} ({fastest.MedianMs:0} ms, mediana).";
            log.Info("Teste de DNS: " + string.Join("; ", results.Select(r => $"{r.Provider.Name}={r.MedianMs?.ToString("0") ?? "-"}ms ({r.Answered}/{r.Asked})")));
        }
        catch (Exception ex)
        {
            log.Error("Teste de DNS", ex);
            Status = "Não deu para testar: " + ex.Message;
        }
        finally { IsBusy = false; }
    }

    [RelayCommand]
    private async Task UseAsync(DnsProviderItem? item)
    {
        if (item is null || IsBusy || item.IsCurrent) return;
        var tweak = TweakCatalog.Get(item.Provider.TweakId);
        if (!elevation.IsElevated)
        {
            if (dialogs.Confirm("Abrir como administrador?", "Trocar o DNS exige administrador. O NinjaOptimizer reabre nesta mesma aba.", confirmText: "Reabrir como administrador")
                && WindowsElevationService.RelaunchElevated("--page dns"))
                Application.Current.Shutdown();
            return;
        }
        if (!dialogs.Confirm($"Usar {item.Name}?", tweak.Description, [.. tweak.Consequences.Select(c => "• " + c), "• Pode desfazer no histórico de alterações."],
                DialogKind.Danger, "Usar este DNS"))
            return;

        IsBusy = true;
        try
        {
            // Só um DNS do app ativo por vez: desfaz o anterior antes (assim o Desfazer sempre volta ao DNS original).
            foreach (var e in engine.Journal.ActiveEntries.Where(e => DnsProviders.TweakIds.Contains(e.TweakId)).Reverse().ToList())
            {
                var r = await engine.RevertAsync(e.Id);
                if (!r.Success) throw new InvalidOperationException(r.Message);
            }
            var report = await Task.Run(() => engine.ApplyAsync([tweak], new ApplyOptions
            {
                CreateRestorePoint = settings.CreateRestorePoint,
                AbortIfRestorePointFails = settings.AbortIfRestorePointFails,
            }));
            var res = report.Results.SingleOrDefault();
            if (report.Aborted || res?.Outcome == TweakOutcome.Failed) throw new InvalidOperationException(report.AbortReason ?? res?.Message);
            log.Info($"DNS trocado para {item.Name}");
            Status = $"Agora usando {item.Name}.";
        }
        catch (Exception ex)
        {
            log.Error("Trocar DNS", ex);
            dialogs.Info("Não deu para trocar o DNS", ex.Message, kind: DialogKind.Danger);
        }
        finally { IsBusy = false; }
        await RefreshAsync();
        if (Changed is { } c) await c();
    }
}
