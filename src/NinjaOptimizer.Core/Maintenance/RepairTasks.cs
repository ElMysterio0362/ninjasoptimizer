using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization;

namespace NinjaOptimizer.Core.Maintenance;

/// <summary>Tipo de início padrão do Windows para serviços que os reparos podem religar.</summary>
public static class DefaultServices
{
    // NlaSvc ficou de fora: o padrão muda entre Windows 10 e 11 e não foi confirmado.
    public static readonly IReadOnlyList<(string Name, string Purpose, ServiceStartMode Mode)> Essential =
    [
        ("bthserv", "Bluetooth", ServiceStartMode.Manual),
        ("WlanSvc", "Wi-Fi", ServiceStartMode.Automatic),
        ("Dhcp", "Obter IP da rede", ServiceStartMode.Automatic),
        ("Netman", "Conexões de rede", ServiceStartMode.Manual),
        ("RmSvc", "Modo avião", ServiceStartMode.Manual),
        ("Audiosrv", "Áudio", ServiceStartMode.Automatic),
        ("AudioEndpointBuilder", "Dispositivos de áudio", ServiceStartMode.Automatic),
        ("BITS", "Downloads do Windows Update e da Store", ServiceStartMode.Manual),
        ("wuauserv", "Windows Update", ServiceStartMode.Manual),
        ("FrameServer", "Câmera", ServiceStartMode.Manual),
        ("XblAuthManager", "Login Xbox", ServiceStartMode.Manual),
        ("XblGameSave", "Saves do Xbox", ServiceStartMode.Manual),
        ("XboxNetApiSvc", "Rede Xbox", ServiceStartMode.Manual),
        ("XboxGipSvc", "Controles Xbox", ServiceStartMode.Manual),
    ];

    public static readonly IReadOnlyList<(string Name, ServiceStartMode Mode)> Store =
    [
        ("BITS", ServiceStartMode.Manual),
        ("wuauserv", ServiceStartMode.Manual),
        ("ClipSVC", ServiceStartMode.Manual),
        ("InstallService", ServiceStartMode.Manual),
    ];

    /// <summary>Volta ao padrão só os que estão Desativados (e não foram desativados por um ajuste ativo do app).</summary>
    public static List<string> RestoreDisabled(IServiceManager services, IEnumerable<(string Name, ServiceStartMode Mode)> table, IReadOnlySet<string> keep)
    {
        var changes = new List<string>();
        foreach (var (name, mode) in table)
        {
            if (keep.Contains(name) || !services.Exists(name)) continue;
            if (services.GetStartMode(name) != ServiceStartMode.Disabled) continue;
            services.SetStartMode(name, mode);
            changes.Add($"{name}: Desativado → {Label(mode)}");
        }
        return changes;
    }

    public static string Label(ServiceStartMode m) => m switch
    {
        ServiceStartMode.Automatic => "Automático",
        ServiceStartMode.AutomaticDelayed => "Automático (atraso)",
        ServiceStartMode.Manual => "Manual",
        ServiceStartMode.Disabled => "Desativado",
        _ => m.ToString(),
    };
}

/// <summary>F1: DISM /RestoreHealth e depois sfc /scannow.</summary>
public sealed class RepairSystemFilesTask(ICommandRunner commands, Func<CancellationToken, Task<int>> runSfc) : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["O PC fica mais lento enquanto a verificação roda.", "Arquivos do Windows corrompidos são trocados por cópias originais: mudanças feitas à mão em arquivos do sistema se perdem."];
    public override string Id => "maintenance.repair-system-files";
    public override string Name => "Verificar e reparar arquivos do Windows";
    public override string Description => "Confere os arquivos do Windows e troca os corrompidos por cópias boas (DISM e SFC). Útil quando o Windows dá erros estranhos ou trava.";
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override string Note => "Precisa de internet: o DISM baixa os arquivos bons do Windows Update. Deixe o PC ligado até terminar.";
    public override bool RequiresAdmin => true;
    public override string Duration => "10 a 30 minutos";
    public override string ActionText => "Verificar agora";

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Reparando a imagem do Windows (DISM)…");
        var dism = await commands.RunAsync("DISM.exe", "/Online /Cleanup-Image /RestoreHealth", ct);
        progress?.Report("Verificando os arquivos (SFC)…");
        var sfc = await runSfc(ct);
        return Interpret(dism.ExitCode, sfc);
    }

    /// <summary>A saída do sfc vem em UTF-16 e é traduzida: confiamos só nos códigos de saída.</summary>
    public static MaintenanceResult Interpret(int dismExit, int sfcExit)
    {
        const string log = @"%windir%\Logs\CBS\CBS.log";
        if (dismExit != 0)
            return new(false, 0, $"O DISM não conseguiu reparar a imagem do Windows (código {dismExit}). Confira a internet e tente de novo. Detalhes em {log}.");
        return sfcExit switch
        {
            0 => new(true, 0, "Nenhum problema encontrado nos arquivos do Windows."),
            1 => new(true, 0, "Problemas encontrados e corrigidos. Reinicie o PC.") { RequiresRestart = true },
            _ => new(false, 0, $"Foram encontrados problemas que não puderam ser corrigidos. Detalhes em {log}."),
        };
    }
}

/// <summary>F3: apaga só os atalhos do primeiro nível da pasta Recentes (nunca AutomaticDestinations).</summary>
public sealed class RecentItemsTask(string recentFolder) : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["A lista de arquivos recentes do Acesso Rápido e do Iniciar é apagada sem volta."];
    public override string Id => "cleanup.recent-items";
    public override string Name => "Limpar a lista de itens recentes";
    public override string Description => "Apaga a lista de arquivos abertos recentemente.";
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override string Note => "Os arquivos recentes somem do Acesso Rápido e do menu Iniciar. Seus arquivos não são apagados, e as pastas fixadas continuam.";

    public const string JumpLists = "jumplists";
    public override IReadOnlyList<MaintenanceOption> Options =>
    [
        new(JumpLists, "Também limpar listas de atalho e fixados",
            ["As pastas fixadas no Acesso Rápido e os itens fixados nas listas dos programas são apagados."]),
    ];

    private IEnumerable<FileInfo> Shortcuts()
    {
        if (!Directory.Exists(recentFolder)) return [];
        var files = new DirectoryInfo(recentFolder).EnumerateFiles("*.lnk", SearchOption.TopDirectoryOnly);
        if (!IsSelected(JumpLists)) return files;
        var lists = new[] { "AutomaticDestinations", "CustomDestinations" }
            .Select(d => Path.Combine(recentFolder, d)).Where(Directory.Exists)
            .SelectMany(d => new DirectoryInfo(d).EnumerateFiles("*", SearchOption.TopDirectoryOnly));
        return files.Concat(lists);
    }

    public override Task<long?> EstimateAsync(CancellationToken ct) => Task.FromResult<long?>(Shortcuts().Sum(f => f.Length));

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        long bytes = 0;
        int files = 0, skipped = 0;
        foreach (var f in Shortcuts())
        {
            try { var len = f.Length; f.Delete(); bytes += len; files++; }
            catch (IOException) { skipped++; }
            catch (UnauthorizedAccessException) { skipped++; }
        }
        return Task.FromResult(new MaintenanceResult(true, bytes, $"{files} item(ns) recente(s) removido(s)." + (skipped > 0 ? $" {skipped} em uso pulado(s)." : "")));
    }
}

/// <summary>F4: renomeia SoftwareDistribution e catroot2 com data e hora e religa os serviços.</summary>
public sealed class RepairWindowsUpdateTask(IServiceManager services, string softwareDistribution, string catroot2, TimeProvider? time = null) : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["O histórico do Windows Update fica vazio.", "Atualizações baixadas e ainda não instaladas são baixadas de novo."];
    private static readonly string[] Services = ["wuauserv", "bits", "cryptsvc"];

    public override string Id => "maintenance.repair-windows-update";
    public override string Name => "Reparar o Windows Update";
    public override string Description => "Para o Windows Update, guarda as pastas de download e de assinaturas com outro nome e começa do zero. Resolve atualizações que falham sempre.";
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override string Note => "O histórico mostrado no Windows Update fica vazio. As atualizações instaladas não são afetadas. Reinicie o PC depois.";
    public override bool RequiresAdmin => true;
    public override string Duration => "cerca de 1 minuto";
    public override string ActionText => "Reparar agora";

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var stamp = (time ?? TimeProvider.System).GetLocalNow().ToString("yyyyMMdd-HHmmss");
        var renamed = new List<string>();
        var failed = new List<string>();
        progress?.Report("Parando o Windows Update…");
        try
        {
            foreach (var s in Services.Where(services.Exists))
                if (services.IsRunning(s)) services.Stop(s);
            foreach (var folder in new[] { softwareDistribution, catroot2 })
            {
                if (!Directory.Exists(folder)) continue;
                try { Directory.Move(folder, $"{folder}.bak-{stamp}"); renamed.Add(Path.GetFileName(folder)); }
                catch (IOException) { failed.Add(Path.GetFileName(folder)); }
                catch (UnauthorizedAccessException) { failed.Add(Path.GetFileName(folder)); }
            }
        }
        finally
        {
            // Religa sempre, mesmo se a renomeação falhar. O tipo de início não muda.
            foreach (var s in Services.Where(services.Exists))
            {
                try { if (services.GetStartMode(s) != ServiceStartMode.Disabled) services.Start(s); } catch { /* o Windows liga quando precisar */ }
            }
        }
        if (failed.Count > 0)
            return new MaintenanceResult(false, 0, $"Não deu para renomear {string.Join(" e ", failed)} (pasta em uso). Os serviços foram religados; reinicie o PC e tente de novo.");
        return new MaintenanceResult(true, 0, renamed.Count == 0 ? "Nada a renomear; serviços religados."
            : $"{string.Join(" e ", renamed)} guardadas como .bak-{stamp}. Reinicie o PC.") { RequiresRestart = true };
    }, ct);
}

/// <summary>F5: winsock reset, ip reset e flush de DNS, salvando antes a configuração atual no log.</summary>
public sealed class RepairNetworkTask(ICommandRunner commands, string logFolder, Func<IReadOnlySet<string>> activeTweakIds) : MaintenanceTask
{
    public const string Renew = "renew";
    public override IReadOnlyList<MaintenanceOption> Options =>
        [new(Renew, "Também pedir um novo IP ao roteador", ["A internet cai por alguns segundos. Não melhora o ping; só ajuda se houver conflito de IP."])];
    public override IReadOnlyList<string> Consequences => ["Apaga IP fixo e DNS configurados à mão.", "Pode remover filtros antigos de VPN ou antivírus.", "Precisa reiniciar o PC para a internet voltar ao normal."];
    public override string Id => "maintenance.repair-network";
    public override string Name => "Reparar a rede";
    public override string Description => "Reinstala as configurações de rede do Windows do zero (Winsock e TCP/IP) e limpa o cache de DNS. Use só se a internet não funciona.";
    public override TweakCategory Category => TweakCategory.Network;
    public override bool RequiresAdmin => true;
    public override string ActionText => "Reparar agora";

    public override string Note
    {
        get
        {
            var note = "Apaga IP fixo e DNS configurados à mão e pode remover filtros antigos de VPN/antivírus. A configuração atual é salva no log antes. Precisa reiniciar.";
            var ids = activeTweakIds();
            if (ids.Contains("network.dns-cloudflare") || ids.Contains("network.nagle-off"))
                note += " O reparo também desfaz, por fora do app, os ajustes de DNS/Nagle aplicados.";
            return note;
        }
    }

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        progress?.Report("Salvando a configuração atual…");
        Directory.CreateDirectory(logFolder);
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var before = await commands.RunAsync("ipconfig", "/all", ct);
        await File.WriteAllTextAsync(Path.Combine(logFolder, $"rede-antes-do-reparo-{stamp}.txt"), before.StdOut, ct);

        progress?.Report("Reparando a rede…");
        var steps = new List<(string File, string Args)>
        {
            ("netsh", "winsock reset"),
            ("netsh", $"int ip reset \"{Path.Combine(logFolder, "netsh-ip-reset.log")}\""),
            ("ipconfig", "/flushdns"),
        };
        if (IsSelected(Renew)) steps.AddRange([("ipconfig", "/release"), ("ipconfig", "/renew")]);
        var errors = new List<string>();
        foreach (var (file, args) in steps)
        {
            var r = await commands.RunAsync(file, args, ct);
            if (!r.Success) errors.Add($"{file} {(args.StartsWith("int ip") ? "int ip reset" : args)} (código {r.ExitCode})");
        }
        return errors.Count == 0
            ? new MaintenanceResult(true, 0, $"Rede reparada. Reinicie o PC. A configuração anterior ficou em rede-antes-do-reparo-{stamp}.txt na pasta de logs.") { RequiresRestart = true }
            : new MaintenanceResult(false, 0, "Parte do reparo falhou: " + string.Join("; ", errors) + ". Reinicie o PC mesmo assim.") { RequiresRestart = true };
    }
}

/// <summary>F6: chkdsk /scan (online). Com erro que precisa de correção offline, oferece /spotfix.</summary>
public sealed class CheckDiskTask(ICommandRunner commands, string drive, Func<string?> fileSystem, char yesLetter = 'Y') : MaintenanceTask
{
    public const string FullAtBoot = "full-at-boot";
    public override IReadOnlyList<MaintenanceOption> Options =>
    [
        new(FullAtBoot, "Verificação completa no próximo boot (chkdsk /f /r)",
            ["O PC fica inutilizável durante a verificação, que pode levar horas num HD grande. Não desligue no meio."], HighRisk: true),
    ];
    // O chkdsk pergunta "agendar no próximo boot? (S/N)" com a letra traduzida.
    private readonly char _yes = char.IsAsciiLetter(yesLetter) ? char.ToUpperInvariant(yesLetter) : throw new ArgumentException("Letra inválida.", nameof(yesLetter));
    public override bool HasSideEffects => false;
    public override IReadOnlyList<string> Consequences => IsSelected(FullAtBoot) ? Options[0].Consequences : [];
    // Só "C:" e afins entram nos comandos.
    private readonly string drive = drive is [var l, ':'] && char.IsAsciiLetter(l) ? drive.ToUpperInvariant()
        : throw new ArgumentException("Unidade inválida.", nameof(drive));

    public override string Id => "maintenance.check-disk";
    public override string Name => "Verificar erros no disco do Windows";
    public override string Description => $"Procura erros no sistema de arquivos da unidade {drive} sem travar o PC nem reiniciar.";
    public override TweakCategory Category => TweakCategory.Disk;
    public override bool RequiresAdmin => true;
    public override string Duration => "alguns minutos";
    public override string ActionText => "Verificar agora";

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        if (!string.Equals(fileSystem(), "NTFS", StringComparison.OrdinalIgnoreCase))
            return new MaintenanceResult(false, 0, $"A verificação online só funciona em NTFS, e {drive} não é NTFS.");
        if (IsSelected(FullAtBoot))
        {
            var full = await commands.RunAsync("cmd.exe", $"/c echo {_yes}| chkdsk.exe {drive} /f /r", ct);
            return full.ExitCode is 0 or 3
                ? new MaintenanceResult(true, 0, $"Verificação completa de {drive} agendada para o próximo boot. Reinicie quando puder deixar o PC parado.") { RequiresRestart = true }
                : new MaintenanceResult(false, 0, $"Não deu para agendar a verificação completa (código {full.ExitCode}).");
        }
        progress?.Report($"Verificando {drive}…");
        var r = await commands.RunAsync("chkdsk.exe", $"{drive} /scan", ct);
        return r.ExitCode switch
        {
            0 => new MaintenanceResult(true, 0, "Nenhum erro encontrado."),
            1 => new MaintenanceResult(true, 0, "Erros encontrados e corrigidos."),
            2 => new MaintenanceResult(true, 0, "Verificação concluída com uma limpeza de rotina."),
            3 => new MaintenanceResult(false, 0, "Foram encontrados erros que só podem ser corrigidos com o disco parado.")
            {
                FollowUp = new("Corrigir no próximo início do Windows?",
                    $"O chkdsk /spotfix corrige só os pontos com erro na próxima vez que o PC ligar. Costuma levar poucos segundos a alguns minutos.",
                    "Agendar correção", async c =>
                    {
                        // "chkdsk /spotfix" pergunta S/N num texto traduzido; o Repair-Volume agenda sem perguntar.
                        var s = await commands.RunAsync("powershell",
                            $"-NoProfile -NonInteractive -Command \"Repair-Volume -DriveLetter {drive[0]} -SpotFix -ErrorAction Stop\"", c);
                        return s.Success
                            ? new MaintenanceResult(true, 0, "Correção agendada. Reinicie o PC quando puder.") { RequiresRestart = true }
                            : new MaintenanceResult(false, 0, $"Não deu para agendar a correção (código {s.ExitCode}).");
                    }),
            },
            _ => new MaintenanceResult(false, 0, $"O chkdsk terminou com o código {r.ExitCode}."),
        };
    }
}

/// <summary>F7: reinicia Audiosrv e AudioEndpointBuilder na ordem certa.</summary>
public sealed class RestartAudioTask(IServiceManager services) : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["O som some por alguns segundos, inclusive em chamadas em andamento."];
    public override string Id => "maintenance.restart-audio";
    public override string Name => "Reiniciar o áudio do Windows";
    public override string Description => "Reinicia os serviços de áudio. Corrige \"nenhum dispositivo de saída de áudio instalado\" e som que parou do nada.";
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override string Note => "O som some por alguns segundos.";
    public override bool RequiresAdmin => true;
    public override string ActionText => "Reiniciar agora";

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var changes = new List<string>();
        foreach (var name in new[] { "Audiosrv", "AudioEndpointBuilder" })
        {
            if (!services.Exists(name)) continue;
            if (services.IsRunning(name)) services.Stop(name);
            if (services.GetStartMode(name) == ServiceStartMode.Disabled)
            {
                services.SetStartMode(name, ServiceStartMode.Automatic);
                changes.Add($"{name}: Desativado → Automático");
            }
        }
        foreach (var name in new[] { "AudioEndpointBuilder", "Audiosrv" })
            if (services.Exists(name)) services.Start(name);
        return new MaintenanceResult(true, 0, "Áudio reiniciado." + (changes.Count > 0 ? " " + string.Join("; ", changes) + "." : ""));
    }, ct);
}

/// <summary>F8: serviços essenciais desativados voltam ao padrão do Windows.</summary>
public sealed class RestoreDefaultServicesTask(IServiceManager services, Func<IReadOnlySet<string>> disabledByApp) : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["Serviços que você desativou de propósito por fora do app (ex.: Bluetooth) voltam a ligar."];
    public override string Id => "maintenance.restore-default-services";
    public override string Name => "Restaurar serviços essenciais ao padrão";
    public override string Description =>
        "Religa, com o tipo de início padrão do Windows, serviços essenciais que estiverem desativados: Bluetooth, Wi-Fi, rede, modo avião, áudio, Windows Update, câmera e Xbox. Os que já estão Manual ou Automático não mudam.";
    public override TweakCategory Category => TweakCategory.ProcessesServices;
    public override string Note => "Serviços que o próprio NinjaOptimizer desativou não são tocados: para eles use Desfazer no histórico.";
    public override bool RequiresAdmin => true;
    public override string ActionText => "Restaurar agora";

    public override Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct) => Task.Run(() =>
    {
        var changes = DefaultServices.RestoreDisabled(services, DefaultServices.Essential.Select(e => (e.Name, e.Mode)), disabledByApp());
        return new MaintenanceResult(true, 0, changes.Count == 0
            ? "Todos os serviços essenciais já estavam ligados."
            : "Restaurados: " + string.Join("; ", changes) + ".");
    }, ct);
}

/// <summary>F9: serviços da Store, wsreset e re-registro só da Store e do Instalador de Aplicativos do usuário atual.</summary>
public sealed class RepairStoreTask(ICommandRunner commands, IServiceManager services, IElevationService elevation, Func<IReadOnlySet<string>> disabledByApp) : MaintenanceTask
{
    public override IReadOnlyList<string> Consequences => ["O cache da Store é apagado: a primeira abertura fica mais lenta.", "A Store abre sozinha no fim."];
    internal const string Reregister =
        "-NoProfile -NonInteractive -Command \"foreach ($n in 'Microsoft.WindowsStore','Microsoft.DesktopAppInstaller') { " +
        "Get-AppxPackage $n | ForEach-Object { Add-AppxPackage -DisableDevelopmentMode -Register \\\"$($_.InstallLocation)\\AppXManifest.xml\\\" } }\"";

    public const string AllApps = "all-apps";
    internal const string ReregisterAll =
        "-NoProfile -NonInteractive -Command \"Get-AppxPackage -AllUsers | ForEach-Object { " +
        "Add-AppxPackage -DisableDevelopmentMode -Register \\\"$($_.InstallLocation)\\AppXManifest.xml\\\" -ErrorAction SilentlyContinue }\"";
    public override IReadOnlyList<MaintenanceOption> Options =>
        [new(AllApps, "Re-registrar todos os apps do sistema (todas as contas)", ["Pode levar vários minutos e mostrar muitos erros, que são normais."], RequiresAdmin: true)];

    public override string Id => "maintenance.repair-store";
    public override string Name => "Reparar a Microsoft Store";
    public override string Description => "Limpa o cache da Store (wsreset) e registra de novo a Store e o Instalador de Aplicativos. Resolve downloads travados e a Store que não abre.";
    public override TweakCategory Category => TweakCategory.SystemPrivacy;
    public override string Note => "A Store abre sozinha no fim.";
    public override string Duration => "1 a 2 minutos";
    public override string ActionText => "Reparar agora";

    public override async Task<MaintenanceResult> RunAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var notes = new List<string>();
        var disabled = DefaultServices.Store.Where(s => services.Exists(s.Name) && services.GetStartMode(s.Name) == ServiceStartMode.Disabled
                                                        && !disabledByApp().Contains(s.Name)).ToList();
        if (disabled.Count > 0)
        {
            if (elevation.IsElevated) notes.AddRange(DefaultServices.RestoreDisabled(services, disabled, new HashSet<string>()));
            else notes.Add($"{string.Join(", ", disabled.Select(d => d.Name))} desativado(s): abra o app como administrador para religar");
        }
        progress?.Report("Limpando o cache da Store…");
        var reset = await commands.RunAsync("wsreset.exe", "", ct);
        progress?.Report("Registrando a Store de novo…");
        var reg = await commands.RunAsync("powershell", Reregister, ct);
        if (IsSelected(AllApps))
        {
            progress?.Report("Registrando todos os apps de novo…");
            if (elevation.IsElevated) await commands.RunAsync("powershell", ReregisterAll, ct); // erros de pacotes individuais são normais
            else notes.Add("re-registrar todos os apps precisa de administrador");
        }
        var ok = reset.Success && reg.Success;
        var msg = ok ? "Store reparada." : $"Parte do reparo falhou (wsreset {reset.ExitCode}, registro {reg.ExitCode}).";
        if (notes.Count > 0) msg += " " + string.Join("; ", notes) + ".";
        return new MaintenanceResult(ok, 0, msg);
    }
}

public static class JournalQueries
{
    /// <summary>Serviços mexidos por ajustes ainda ativos (os reparos não passam por cima deles).</summary>
    public static IReadOnlySet<string> ServicesChangedByApp(Safety.ChangeJournal journal) =>
        journal.ActiveEntries.SelectMany(e => e.Snapshots)
            .Where(s => s.Kind == Optimization.Actions.ServiceStartupAction.KindId)
            .Select(s => s.Get("service")).OfType<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlySet<string> ActiveTweakIds(Safety.ChangeJournal journal) =>
        journal.ActiveEntries.Select(e => e.TweakId).ToHashSet();
}
