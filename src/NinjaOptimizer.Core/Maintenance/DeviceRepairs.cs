using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Debloat;
using NinjaOptimizer.Core.Optimization;

namespace NinjaOptimizer.Core.Maintenance;

/// <summary>Fecha programas abertos (primeiro pedindo, só força se o usuário escolher).</summary>
public interface IAppCloser
{
    IReadOnlyList<(int Pid, string Name)> Running(IEnumerable<string> processNames);
    /// <summary>Pede para fechar (como clicar no X). true = fechou dentro do tempo.</summary>
    bool CloseGracefully(int pid, TimeSpan wait);
    void Kill(int pid);
}

/// <summary>Re-registra pacotes Appx já instalados da conta atual (nomes validados antes).</summary>
internal static class AppxReregister
{
    public static string Command(string name) =>
        AppCatalog.IsValidPackageName(name)
            ? "-NoProfile -NonInteractive -Command \"Get-AppxPackage " + name + " | ForEach-Object { Add-AppxPackage -DisableDevelopmentMode -Register " +
              "\\\"$($_.InstallLocation)\\AppXManifest.xml\\\" }\""
            : throw new ArgumentException("Nome de pacote inválido.", nameof(name));
}

/// <summary>K3: serviços Xbox fora de Desativado, reinício dos que rodam e re-registro só dos apps Xbox.</summary>
public sealed class RepairXboxAppsTask(ICommandRunner commands, IServiceManager services, Func<bool> xboxTweakActive,
    Func<CancellationToken, Task<MaintenanceResult>> undoXboxTweak) : MaintenanceTask
{
    internal static readonly string[] Services = ["XblAuthManager", "XblGameSave", "XboxNetApiSvc", "XboxGipSvc"];
    internal static readonly string[] Packages = ["Microsoft.GamingApp", "Microsoft.XboxGamingOverlay", "Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI"];

    public override string Id => "maintenance.repair-xbox-apps";
    public override string Name => "Reparar os apps Xbox";
    public override string Description => "Religa os serviços Xbox e registra de novo o app Xbox, a Game Bar e o login Xbox. Resolve Game Pass que não abre e login Xbox que falha.";
    public override TweakCategory Category => TweakCategory.Gaming;
    public override bool RequiresAdmin => true;
    public override string ActionText => "Reparar agora";
    public override IReadOnlyList<string> Consequences =>
        ["Jogos e apps Xbox abertos podem perder a conexão com a conta por alguns segundos.", "Serviços Xbox desativados à mão voltam para Manual."];

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (xboxTweakActive())
            return new MaintenanceResult(false, 0, "Os serviços Xbox foram desativados pelo ajuste \"Desativar os serviços Xbox\" do NinjaOptimizer.")
            {
                FollowUp = new("Desfazer o ajuste dos serviços Xbox?", "Desfazer volta os serviços exatamente como estavam antes do ajuste.", "Desfazer o ajuste", undoXboxTweak),
            };

        var notes = DefaultServices.RestoreDisabled(services, Services.Select(s => (s, ServiceStartMode.Manual)), new HashSet<string>());
        foreach (var s in Services.Where(s => services.Exists(s) && services.IsRunning(s)))
        {
            try { services.Stop(s); services.Start(s); } catch (Exception) { notes.Add($"{s} não reiniciou"); }
        }
        progress?.Report("Registrando os apps Xbox de novo…");
        var failed = new List<string>();
        foreach (var p in Packages)
        {
            var r = await commands.RunAsync("powershell", AppxReregister.Command(p), ct);
            if (!r.Success) failed.Add(p);
        }
        var msg = failed.Count == 0 ? "Apps Xbox reparados." : $"Não deu para registrar: {string.Join(", ", failed)}.";
        if (notes.Count > 0) msg += " " + string.Join("; ", notes) + ".";
        return new MaintenanceResult(failed.Count == 0, 0, msg);
    }
}

/// <summary>Configuração de um reparo de dispositivo (câmera ou microfone).</summary>
public sealed record DeviceRepairKind(
    string Id, string Name, string Description, string[] DeviceClasses, string? ExtraClass, string ExtraClassTitle, string[] ExtraClassConsequences,
    string[] ServicesInOrder, string? AppPackage, string SettingsUri, string AllowTweakId, string AllowAllUsersTweakId, string RemovePolicyTweakId,
    string What, string TheWhat, string YourWhat)
{
    public static DeviceRepairKind Camera { get; } = new(
        "maintenance.repair-camera", "Reparar a câmera",
        "Fecha (com sua permissão) os programas que podem estar usando a câmera, reinicia o serviço da câmera, desliga e liga a webcam e registra de novo o app Câmera.",
        ["Camera", "Image"], null, "", [],
        ["FrameServer", "FrameServerMonitor"], "Microsoft.WindowsCamera", "ms-settings:privacy-webcam",
        "privacy.camera-allow-apps", "privacy.camera-allow-apps-all-users", "privacy.camera-remove-policy", "câmera", "a câmera", "sua câmera");

    public static DeviceRepairKind Microphone { get; } = new(
        "maintenance.repair-microphone", "Reparar o microfone",
        "Fecha (com sua permissão) os programas que podem estar usando o microfone, reinicia o áudio do Windows e desliga e liga os dispositivos de áudio.",
        ["AudioEndpoint"], "MEDIA", "Incluir placas de som, áudio HDMI e placas de captura (classe MEDIA)",
        ["Inclui o áudio HDMI da placa de vídeo, placas de captura e interfaces de áudio: o som delas cai por alguns segundos."],
        ["Audiosrv", "AudioEndpointBuilder"], null, "ms-settings:sound",
        "privacy.microphone-allow-apps", "privacy.microphone-allow-apps-all-users", "privacy.microphone-remove-policy", "microfone", "o microfone", "seu microfone");
}

/// <summary>K4/K5: reparo da câmera ou do microfone, passo a passo, cada passo no resultado.</summary>
public sealed class DeviceRepairTask(DeviceRepairKind kind, ICommandRunner commands, IServiceManager services, IAppCloser closer,
    Func<string, CancellationToken, Task<string>> applyTweak) : MaintenanceTask
{
    public const string AllowApps = "allow-apps", AllowAllUsers = "allow-all-users", RemovePolicy = "remove-policy", ExtraClass = "extra-class", DriverScan = "driver-scan";

    /// <summary>Programas que costumam segurar a câmera/microfone.</summary>
    internal static readonly string[] Holders = ["ms-teams", "Teams", "Zoom", "Discord", "Skype", "obs64", "obs32", "WindowsCamera"];

    public override string Id => kind.Id;
    public override string Name => kind.Name;
    public override string Description => kind.Description;
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override bool RequiresAdmin => true;
    public override string Duration => "menos de 1 minuto";
    public override string ActionText => "Reparar agora";
    public override IReadOnlyList<string> Consequences =>
    [
        "Programas que você escolher fechar perdem trabalho não salvo e chamadas em andamento.",
        $"O dispositivo de {kind.What} é desligado e ligado de novo: some por alguns segundos.",
    ];

    public override IReadOnlyList<MaintenanceOption> Options
    {
        get
        {
            var list = new List<MaintenanceOption>();
            if (kind.ExtraClass is not null) list.Add(new(ExtraClass, kind.ExtraClassTitle, kind.ExtraClassConsequences));
            list.Add(new(AllowApps, $"Liberar {kind.TheWhat} para os apps",
                [$"Todos os apps passam a poder usar {kind.YourWhat}: revise em Configurações → Privacidade.", "Fica no histórico com Desfazer."]));
            list.Add(new(AllowAllUsers, $"Liberar {kind.TheWhat} para todos os usuários deste PC",
                [$"Os apps de todas as contas deste PC passam a poder usar {kind.TheWhat}."], RequiresAdmin: true));
            list.Add(new(RemovePolicy, "Remover bloqueios de política",
                ["Em PC de empresa ou escola, isso desfaz uma regra do administrador, e ela pode voltar na próxima sincronização."], HighRisk: true, RequiresAdmin: true));
            list.Add(new(DriverScan, "Procurar drivers no Windows Update", ["O Windows Update pode baixar e instalar drivers em segundo plano."]));
            return list;
        }
    }

    private string Classes => string.Join(",", kind.DeviceClasses.Concat(IsSelected(ExtraClass) && kind.ExtraClass is { } x ? [x] : []).Select(c => $"'{c}'"));

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var steps = new List<string>();

        // 1) Programas que podem estar usando o dispositivo: pergunta quais fechar.
        var running = closer.Running(Holders);
        if (running.Count > 0)
        {
            var names = running.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var chosen = Ui is null ? [] : await Ui.ChooseAsync($"Fechar programas que usam {kind.TheWhat}?",
                "Trabalho não salvo e chamadas em andamento nesses programas são perdidos. Desmarque o que não quer fechar.", names, "Fechar marcados") ?? [];
            var left = new List<(int Pid, string Name)>();
            foreach (var p in running.Where(r => chosen.Contains(r.Name, StringComparer.OrdinalIgnoreCase)))
                if (!closer.CloseGracefully(p.Pid, TimeSpan.FromSeconds(5))) left.Add(p);
            if (left.Count > 0 && Ui is not null && await Ui.ConfirmAsync("Forçar o fechamento?",
                    "Estes programas não fecharam sozinhos. Forçar perde tudo o que não foi salvo neles.", [.. left.Select(l => l.Name).Distinct()], "Forçar"))
                foreach (var p in left) closer.Kill(p.Pid);
            steps.Add(chosen.Count == 0 ? "Nenhum programa fechado." : $"Fechados: {string.Join(", ", chosen)}.");
        }

        // 2) Serviços, na ordem certa, sem mudar o tipo de início.
        progress?.Report("Reiniciando serviços…");
        foreach (var s in kind.ServicesInOrder.Where(s => services.Exists(s) && services.IsRunning(s))) services.Stop(s);
        foreach (var s in kind.ServicesInOrder.Reverse().Where(s => services.Exists(s) && services.GetStartMode(s) != ServiceStartMode.Disabled))
        {
            try { services.Start(s); } catch (Exception) { steps.Add($"{s} não iniciou."); }
        }
        steps.Add("Serviços reiniciados.");

        // 3) Desliga e liga só os dispositivos das classes escolhidas.
        progress?.Report("Reiniciando os dispositivos…");
        var cycle = await commands.RunAsync("powershell",
            $"-NoProfile -NonInteractive -Command \"Get-PnpDevice -Class {Classes} -PresentOnly -ErrorAction SilentlyContinue | Where-Object {{ $_.Status -ne 'Unknown' }} | " +
            "ForEach-Object { Disable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue; Enable-PnpDevice -InstanceId $_.InstanceId -Confirm:$false -ErrorAction SilentlyContinue }\"", ct);
        steps.Add(cycle.Success ? "Dispositivos reiniciados." : "Não deu para reiniciar todos os dispositivos.");
        await commands.RunAsync("pnputil", "/scan-devices", ct);

        // 4) App do Windows ligado ao dispositivo.
        if (kind.AppPackage is { } pkg && (await commands.RunAsync("powershell", AppxReregister.Command(pkg), ct)).Success)
            steps.Add("App registrado de novo.");

        // 6–7) Opções avançadas viram ajustes no histórico (com Desfazer).
        foreach (var (opt, tweakId) in new[] { (AllowApps, kind.AllowTweakId), (AllowAllUsers, kind.AllowAllUsersTweakId), (RemovePolicy, kind.RemovePolicyTweakId) })
            if (IsSelected(opt)) steps.Add(await applyTweak(tweakId, ct));

        // 8) Drivers.
        if (IsSelected(DriverScan)) { await commands.RunAsync("UsoClient", "StartScan", ct); steps.Add("Busca de drivers iniciada no Windows Update."); }

        // 5) Estado final + configurações.
        var state = await commands.RunAsync("powershell",
            $"-NoProfile -NonInteractive -Command \"Get-PnpDevice -Class {Classes} -PresentOnly -ErrorAction SilentlyContinue | ForEach-Object {{ \\\"$($_.FriendlyName): $($_.Status)\\\" }}\"", ct);
        var devices = state.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        steps.Add(devices.Length == 0 ? $"Nenhum dispositivo de {kind.What} encontrado." : "Estado: " + string.Join("; ", devices) + ".");
        await commands.RunAsync("explorer.exe", kind.SettingsUri, ct);

        return new MaintenanceResult(cycle.Success, 0, string.Join(" ", steps));
    }
}

/// <summary>K6: desliga à força quando o PC não desliga normalmente.</summary>
public sealed class ForceShutdownTask(ICommandRunner commands) : MaintenanceTask
{
    public override string Id => "maintenance.force-shutdown";
    public override string Name => "Desligar à força";
    public override string Description => "Desliga o PC na hora, mesmo com programas travados. Para quando o PC não desliga normalmente. Se isso acontece sempre, tente desativar a Inicialização Rápida.";
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override string ActionText => "Desligar agora";
    public override IReadOnlyList<string> Consequences => ["Fecha todos os programas sem salvar nada."];

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var r = await commands.RunAsync("shutdown", "/s /f /t 0", ct);
        return new MaintenanceResult(r.Success, 0, r.Success ? "Desligando…" : $"O Windows recusou o desligamento (código {r.ExitCode}).");
    }
}

/// <summary>Reiniciar agora (depois de ajustes que pedem reinício).</summary>
public static class Restart
{
    public static Task<CommandResult> NowAsync(ICommandRunner commands, CancellationToken ct = default) => commands.RunAsync("shutdown", "/r /t 0", ct);
}
