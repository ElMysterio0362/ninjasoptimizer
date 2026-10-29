using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Optimization.Actions;
using static NinjaOptimizer.Core.Optimization.OptimizationProfile;

namespace NinjaOptimizer.Core.Optimization;

/// <summary>
/// Catálogo de otimizações. Cada item é declarativo: novas otimizações são só novas entradas,
/// reaproveitando as ações (registro, serviço, plano de energia…) e o sistema de reversão.
/// </summary>
public static class TweakCatalog
{
    private const string HKLM_DataCollectionPolicy = @"SOFTWARE\Policies\Microsoft\Windows\DataCollection";
    private const string HKLM_DataCollection = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection";

    public static IReadOnlyList<Tweak> All { get; } = Build();

    public static Tweak Get(string id) => All.First(t => t.Id == id);

    public static IReadOnlyList<Tweak> ForProfile(OptimizationProfile profile) =>
        All.Where(t => t.Profiles.Contains(profile)).ToList();

    private static HashSet<OptimizationProfile> P(params OptimizationProfile[] p) => [.. p];

    // Atalhos para valores de registro
    private static RegistryValueAction CuDword(string key, string name, uint v) => new(RegHive.CurrentUser, key, name, RegValue.Dword(v));
    private static RegistryValueAction CuString(string key, string name, string v) => new(RegHive.CurrentUser, key, name, RegValue.Str(v));
    private static RegistryValueAction LmDword(string key, string name, uint v) => new(RegHive.LocalMachine, key, name, RegValue.Dword(v));
    private static RegistryValueAction LmString(string key, string name, string v) => new(RegHive.LocalMachine, key, name, RegValue.Str(v));

    private const string ContentDelivery = @"Software\Microsoft\Windows\CurrentVersion\ContentDeliveryManager";
    private const string ExplorerAdvanced = @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced";
    private const string GameConfigStore = @"System\GameConfigStore";
    // Plano de energia: subgrupos e configurações (GUIDs fixos do Windows)
    private const string SubProcessor = "54533251-82be-4824-96c1-47b60b740d00";
    private const string CoreParkingMinCores = "0cc5b647-c1df-4637-891a-dec35c318583";
    private const string SubPciExpress = "501a4d13-42af-4429-9fd1-a8218c268e20";
    private const string LinkStatePowerManagement = "ee12f906-d277-404b-b6da-e5fa1a576df5";
    private const string MultimediaProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";
    private const string SubUsb = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSelectiveSuspend = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";
    private const string GraphicsDrivers = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";

    private const string FileSystem = @"SYSTEM\CurrentControlSet\Control\FileSystem";
    private const string MemoryManagement = @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management";

    private static CommandLine MmAgentQuery => new("powershell", "-NoProfile -NonInteractive -Command \"(Get-MMAgent).MemoryCompression\"");
    /// <summary>
    /// "HibernateEnabled" só existe depois que alguém liga/desliga a hibernação; antes disso o Windows
    /// usa "HibernateEnabledDefault". Sem nenhum dos dois, a hibernação está ligada (padrão do Windows).
    /// </summary>
    internal static bool? HibernationEnabled(SystemContext ctx)
    {
        const string key = @"SYSTEM\CurrentControlSet\Control\Power";
        var v = ctx.Registry.GetValue(RegHive.LocalMachine, key, "HibernateEnabled")
                ?? ctx.Registry.GetValue(RegHive.LocalMachine, key, "HibernateEnabledDefault");
        return v?.Data is uint d ? d != 0 : true;
    }

    private static CommandLine HibernateQuery => new("reg", @"query HKLM\SYSTEM\CurrentControlSet\Control\Power /v HibernateEnabled");

    private const string GamesTask = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";

    private static List<Tweak> Build() =>
    [
        // ───────────── Sistema e privacidade ─────────────
        new Tweak
        {
            Id = "privacy.telemetry",
            Name = "Desativar telemetria do Windows",
            Description =
                "Reduz a coleta de dados de diagnóstico ao nível mínimo permitido pela edição do Windows " +
                "e desativa o serviço 'Experiências do Usuário Conectado e Telemetria' (DiagTrack).",
            Benefit = "Menos uso de disco/rede em segundo plano e mais privacidade.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions =
            [
                new RegistryValueAction(RegHive.LocalMachine, HKLM_DataCollectionPolicy, "AllowTelemetry", RegValue.Dword(0)),
                new RegistryValueAction(RegHive.LocalMachine, HKLM_DataCollection, "AllowTelemetry", RegValue.Dword(0)),
                new ServiceStartupAction("DiagTrack", ServiceStartMode.Disabled),
            ],
        },

        // ───────────── Jogos ─────────────
        new Tweak
        {
            Id = "gaming.gamedvr",
            Name = "Desativar Game DVR (gravação em segundo plano)",
            Description =
                "Desliga a captura contínua de jogo do Xbox Game Bar, que grava os últimos minutos de " +
                "jogo o tempo todo. A Game Bar continua abrindo normalmente com Win+G.",
            Benefit = "Libera GPU/codificador de vídeo e pode reduzir quedas de FPS e stutter.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions =
            [
                new RegistryValueAction(RegHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegValue.Dword(0)),
                new RegistryValueAction(RegHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegValue.Dword(0)),
                new RegistryValueAction(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", RegValue.Dword(0)),
            ],
        },

        // ───────────── CPU ─────────────
        new Tweak
        {
            Id = "cpu.ultimate-power-plan",
            Consequences = ["Mais consumo de energia e temperatura; em notebook a bateria dura menos e a ventoinha gira mais."],
            Name = "Plano de energia Desempenho Máximo",
            Description =
                "Cria e ativa o plano 'Desempenho Máximo' (Ultimate Performance), que mantém a CPU em " +
                "frequências altas e elimina micro-latências de economia de energia. Se o Windows não " +
                "permitir, ativa 'Alto desempenho'. Ao reverter, o plano anterior volta a ser o ativo.",
            Benefit = "Resposta mais rápida da CPU e menos variação de frametime em jogos.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.Medium, // aumenta consumo e temperatura, principalmente em notebooks
            Impact = ImpactLevel.High,
            Profiles = P(MaxPerformance, Gamer),
            Actions = [new PowerPlanAction()],
        },
        // ═════════════ Etapa 2A · Sistema e privacidade ═════════════
        new Tweak
        {
            Id = "privacy.ads-tips",
            Name = "Desativar dicas, sugestões e anúncios do Windows",
            Description =
                "Para de mostrar sugestões no menu Iniciar, dicas na tela de bloqueio, telas de \"concluir a configuração\" " +
                "e de instalar apps patrocinados sem você pedir.",
            Benefit = "Menos distrações, menos apps instalados sozinhos e menos uso de rede em segundo plano.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions =
            [
                CuDword(ContentDelivery, "SubscribedContent-338388Enabled", 0), // sugestões no Iniciar
                CuDword(ContentDelivery, "SubscribedContent-338389Enabled", 0), // dicas e truques
                CuDword(ContentDelivery, "SubscribedContent-353694Enabled", 0), // conteúdo sugerido em Configurações
                CuDword(ContentDelivery, "SubscribedContent-353696Enabled", 0),
                CuDword(ContentDelivery, "SystemPaneSuggestionsEnabled", 0),
                CuDword(ContentDelivery, "SilentInstalledAppsEnabled", 0),      // apps patrocinados instalados sozinhos
                CuDword(ContentDelivery, "SoftLandingEnabled", 0),
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\UserProfileEngagement", "ScoobeSystemSettingEnabled", 0),
            ],
        },
        new Tweak
        {
            Id = "privacy.advertising-id",
            Name = "Desativar ID de publicidade e experiências personalizadas",
            Description =
                "Impede que apps usem um identificador de publicidade seu e que a Microsoft use seus dados de " +
                "diagnóstico para personalizar dicas e ofertas.",
            Benefit = "Mais privacidade, sem efeito no desempenho.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions =
            [
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", 0),
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\Privacy", "TailoredExperiencesWithDiagnosticDataEnabled", 0),
            ],
        },
        new Tweak
        {
            Id = "privacy.activity-history",
            Name = "Desativar histórico de atividades",
            Description = "Para de registrar e enviar à Microsoft os apps, arquivos e sites que você abre.",
            Benefit = "Mais privacidade e menos gravações em disco em segundo plano.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions =
            [
                LmDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "PublishUserActivities", 0),
                LmDword(@"SOFTWARE\Policies\Microsoft\Windows\System", "UploadUserActivities", 0),
            ],
        },
        new Tweak
        {
            Id = "privacy.bing-search",
            Name = "Tirar resultados da web (Bing) da busca do Windows",
            Description =
                "A busca do menu Iniciar passa a procurar só no seu PC, sem enviar o que você digita para o Bing.",
            Benefit = "Busca mais rápida e mais privada.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            RequiresRestart = true, // o Explorer só relê ao reiniciar a sessão
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions =
            [
                CuDword(@"Software\Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", 1),
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", 0),
            ],
        },
        new Tweak
        {
            Id = "system.background-apps",
            Consequences = ["Apps da Store (Telefone, Email, Teams da Store) param de mostrar notificações e de sincronizar até serem abertos."],
            Name = "Impedir apps da Microsoft Store em segundo plano",
            Description =
                "Apps da Store (como Fotos, Clima e Notícias) deixam de rodar quando estão fechados. " +
                "Apps desse tipo param de mostrar notificações e de atualizar sozinhos até você abri-los.",
            Benefit = "Libera memória e CPU que esses apps usam escondidos.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Medium, // notificações de apps da Store (ex.: Telefone, Mail) param
            Impact = ImpactLevel.Medium,
            Profiles = P(MaxPerformance, Gamer),
            Actions =
            [
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", 1),
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "BackgroundAppGlobalToggle", 0),
            ],
        },
        new Tweak
        {
            Id = "system.animations",
            Name = "Reduzir animações do Windows",
            Description =
                "Janelas abrem e minimizam sem animação, a barra de tarefas para de animar e a espiada da área de trabalho é desligada.",
            Benefit = "Janelas e menus respondem na hora, principalmente em PCs mais modestos.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            RequiresRestart = true, // parte vale só ao sair e entrar de novo
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions =
            [
                CuString(@"Control Panel\Desktop\WindowMetrics", "MinAnimate", "0"),
                CuDword(ExplorerAdvanced, "TaskbarAnimations", 0),
                CuDword(@"Software\Microsoft\Windows\DWM", "EnableAeroPeek", 0),
            ],
        },
        new Tweak
        {
            Id = "system.menu-delay",
            Name = "Abrir menus mais rápido",
            Description = "Reduz a espera para submenus abrirem ao passar o mouse, de 400 ms para 100 ms.",
            Benefit = "Navegação nos menus mais ágil.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions = [CuString(@"Control Panel\Desktop", "MenuShowDelay", "100")],
        },
        new Tweak
        {
            Id = "system.transparency",
            Name = "Desativar efeitos de transparência",
            Description = "Barra de tarefas, menu Iniciar e janelas ficam opacos, sem o efeito de vidro.",
            Benefit = "Menos trabalho para a GPU, útil em placas de vídeo integradas.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(MaxPerformance),
            Actions = [CuDword(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", 0)],
        },

        // ═════════════ Etapa 2A · Jogos ═════════════
        new Tweak
        {
            Id = "gaming.game-mode",
            Name = "Ativar o Modo de Jogo do Windows",
            Description =
                "Quando um jogo está aberto, o Windows dá prioridade a ele e adia atualizações e instalações de drivers.",
            Benefit = "Menos interrupções e FPS mais estável durante o jogo.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions =
            [
                CuDword(@"Software\Microsoft\GameBar", "AutoGameModeEnabled", 1),
                CuDword(@"Software\Microsoft\GameBar", "AllowAutoGameMode", 1),
            ],
        },
        new Tweak
        {
            Id = "gaming.game-bar-shortcuts",
            Name = "Não abrir a Xbox Game Bar pelo controle",
            Description =
                "O botão Xbox do controle para de abrir a Game Bar no meio do jogo e o painel de boas-vindas não aparece mais. " +
                "A Game Bar continua disponível com Win+G.",
            Benefit = "Evita aberturas acidentais que tiram o foco do jogo.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(MaxPerformance, Gamer),
            Actions =
            [
                CuDword(@"Software\Microsoft\GameBar", "UseNexusForGameBarEnabled", 0),
                CuDword(@"Software\Microsoft\GameBar", "ShowStartupPanel", 0),
            ],
        },
        new Tweak
        {
            Id = "gaming.fullscreen-optimizations",
            Consequences = ["HDR automático, sobreposições do Windows e a troca rápida com Alt+Tab podem parar de funcionar em alguns jogos."],
            Name = "Desativar otimizações de tela cheia",
            Description =
                "Jogos em tela cheia passam a usar o modo exclusivo tradicional, sem a camada do Windows por cima. " +
                "Alguns recursos, como HDR automático e sobreposições do Windows, podem deixar de funcionar nesses jogos.",
            Benefit = "Pode reduzir a latência de entrada e o stutter em jogos mais antigos.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Medium,
            Profiles = P(Gamer),
            Actions =
            [
                CuDword(GameConfigStore, "GameDVR_FSEBehaviorMode", 2),
                CuDword(GameConfigStore, "GameDVR_FSEBehavior", 2),
                CuDword(GameConfigStore, "GameDVR_HonorUserFSEBehaviorMode", 1),
                CuDword(GameConfigStore, "GameDVR_DXGIHonorFSEWindowsCompatible", 1),
            ],
        },
        new Tweak
        {
            Id = "gaming.mouse-acceleration",
            Name = "Desativar aceleração do mouse",
            Description =
                "Desliga \"Aumentar a precisão do ponteiro\": o cursor passa a andar sempre a mesma distância para o mesmo movimento.",
            Benefit = "Mira mais consistente em jogos de tiro.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            RequiresRestart = true, // vale ao sair e entrar de novo
            Profiles = P(Gamer),
            Actions =
            [
                CuString(@"Control Panel\Mouse", "MouseSpeed", "0"),
                CuString(@"Control Panel\Mouse", "MouseThreshold1", "0"),
                CuString(@"Control Panel\Mouse", "MouseThreshold2", "0"),
            ],
        },
        new Tweak
        {
            Id = "gaming.scheduler-priority",
            Name = "Dar prioridade a jogos no agendador multimídia",
            Description =
                "Ajusta o perfil \"Games\" do agendador multimídia do Windows para dar mais prioridade de CPU e GPU às tarefas de jogo.",
            Benefit = "Pode reduzir quedas de FPS quando há outros programas abertos.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(MaxPerformance, Gamer),
            Actions =
            [
                LmDword(GamesTask, "GPU Priority", 8),
                LmDword(GamesTask, "Priority", 6),
                LmString(GamesTask, "Scheduling Category", "High"),
                LmString(GamesTask, "SFIO Priority", "High"),
            ],
        },
        new Tweak
        {
            Id = "gaming.sticky-keys",
            Name = "Desativar o atalho das Teclas de Aderência",
            Description =
                "Apertar Shift cinco vezes deixa de abrir o aviso de Teclas de Aderência no meio do jogo. O recurso continua nas Configurações.",
            Benefit = "Evita que o jogo seja minimizado por acidente.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Gamer),
            Actions = [CuString(@"Control Panel\Accessibility\StickyKeys", "Flags", "506")],
        },
        // ═════════════ Etapa 2B · CPU ═════════════
        new Tweak
        {
            Id = "cpu.core-parking",
            Name = "Manter todos os núcleos da CPU prontos",
            Description =
                "Desliga o \"core parking\": o Windows para de adormecer núcleos da CPU quando a carga cai. " +
                "Vale no plano de energia ativo, com o PC na tomada. Se você trocar de plano, aplique de novo.",
            Benefit = "Menos atraso quando um jogo pede mais núcleos de repente; frametime mais estável.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            Profiles = P(MaxPerformance, Gamer),
            Actions = [new PowerSettingAction(SubProcessor, CoreParkingMinCores, 100, "mínimo de núcleos ativos 100%")],
        },
        new Tweak
        {
            Id = "cpu.foreground-priority",
            Name = "Priorizar o programa em primeiro plano",
            Description =
                "O Windows dá fatias de tempo de CPU mais curtas e um reforço maior ao programa que está na frente, " +
                "como o jogo ou o app que você está usando.",
            Benefit = "O que você está usando responde melhor quando há programas em segundo plano.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions = [LmDword(@"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", 0x26)],
        },
        new Tweak
        {
            Id = "cpu.power-throttling",
            Name = "Desativar a limitação de energia de processos",
            Description =
                "Impede o Windows de colocar programas em segundo plano nos núcleos econômicos ou em frequência reduzida. " +
                "Em notebooks na bateria, gasta um pouco mais.",
            Benefit = "Programas em segundo plano (gravação, Discord, downloads) não engasgam enquanto você joga.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(MaxPerformance, Gamer),
            Actions = [LmDword(@"SYSTEM\CurrentControlSet\Control\Power\PowerThrottling", "PowerThrottlingOff", 1)],
        },

        // ═════════════ Etapa 2B · GPU ═════════════
        new Tweak
        {
            Id = "gpu.hags",
            Consequences = ["Em poucos jogos antigos e alguns programas de captura pode causar engasgos ou tela preta.", "Só vale depois de reiniciar."],
            Name = "Ativar agendamento de GPU acelerado por hardware",
            Description =
                "A própria placa de vídeo organiza a fila de trabalho, em vez do processador. Exige placa e driver " +
                "compatíveis (NVIDIA GTX 1000 ou mais nova, AMD RX 5000 ou mais nova). Em poucos jogos antigos pode causar engasgos.",
            Benefit = "Reduz a latência entre CPU e GPU e alivia a CPU. Necessário para a Geração de Quadros da NVIDIA.",
            Category = TweakCategory.Gpu,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Medium,
            RequiresRestart = true,
            Profiles = P(MaxPerformance, Gamer),
            Actions = [LmDword(GraphicsDrivers, "HwSchMode", 2)],
        },
        new Tweak
        {
            Id = "gpu.windowed-optimizations",
            Name = "Ativar otimizações para jogos em janela",
            Description =
                "Jogos em janela ou em tela cheia sem bordas passam a usar o modo de apresentação mais moderno do DirectX. " +
                "Outras opções gráficas do Windows, como HDR automático, não são alteradas.",
            Benefit = "Menos latência e suporte a taxa de atualização variável em jogos sem bordas.",
            Category = TweakCategory.Gpu,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions = [new DirectXSettingAction("SwapEffectUpgradeEnable", "1")],
        },
        new Tweak
        {
            Id = "gpu.pcie-power",
            Name = "Desativar economia de energia do PCI Express",
            Description =
                "A conexão da placa de vídeo (PCI Express) não entra em modo de economia entre um quadro e outro. " +
                "Vale no plano de energia ativo, com o PC na tomada.",
            Benefit = "Evita pequenos atrasos quando a GPU acorda da economia de energia.",
            Category = TweakCategory.Gpu,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(MaxPerformance, Gamer),
            Actions = [new PowerSettingAction(SubPciExpress, LinkStatePowerManagement, 0, "economia de energia do PCI Express desligada")],
        },
        new Tweak
        {
            Id = "gpu.mpo-off",
            Consequences = ["Em vídeos e no navegador o uso da placa de vídeo pode subir um pouco.", "Em versões recentes do Windows pode não ter efeito."],
            Name = "Desativar o MPO (corrige tela piscando)",
            Description =
                "Desliga as \"sobreposições multiplano\" do Windows. Use só se você vê tela piscando, travadinhas no " +
                "navegador ou em vídeos com placas NVIDIA. Em versões recentes do Windows pode não ter efeito.",
            Benefit = "Resolve piscadas e engasgos causados por um problema conhecido de alguns drivers.",
            Category = TweakCategory.Gpu,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(), // correção específica: fica fora dos perfis
            Actions = [LmDword(@"SOFTWARE\Microsoft\Windows\Dwm", "OverlayTestMode", 5)],
        },
        // ═════════════ Etapa 2C · Disco ═════════════
        new Tweak
        {
            Id = "storage.trim",
            Name = "Garantir o TRIM do SSD ligado",
            Description =
                "O TRIM avisa o SSD quais blocos foram apagados, para ele limpá-los com antecedência. " +
                "Vem ligado no Windows; este ajuste religa se algum programa desligou.",
            Benefit = "Mantém a velocidade de escrita do SSD com o tempo.",
            Category = TweakCategory.Disk,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions = [LmDword(FileSystem, "DisableDeleteNotification", 0)],
        },
        new Tweak
        {
            Id = "storage.last-access",
            Name = "Não gravar a data de último acesso dos arquivos",
            Description =
                "O Windows deixa de atualizar a data de \"último acesso\" toda vez que um arquivo é aberto. " +
                "Poucos programas de backup antigos usam essa informação.",
            Benefit = "Menos gravações no disco, principalmente em pastas com muitos arquivos.",
            Category = TweakCategory.Disk,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions = [LmDword(FileSystem, "NtfsDisableLastAccessUpdate", 0x80000001)],
        },
        new Tweak
        {
            Id = "storage.hibernation-off",
            Consequences = ["O PC deixa de hibernar e a Inicialização Rápida é desligada: ligar o PC do zero fica um pouco mais lento."],
            Name = "Desativar a hibernação",
            Description =
                "Apaga o arquivo de hibernação, que ocupa no disco quase o tamanho da sua RAM. " +
                "O PC deixa de hibernar e a inicialização rápida do Windows é desligada.",
            Benefit = "Libera vários GB no disco do Windows (cerca de 6 a 13 GB num PC com 16 GB de RAM).",
            Category = TweakCategory.Disk,
            Risk = RiskLevel.Medium, // notebooks perdem a hibernação; o boot a frio fica um pouco mais lento
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Actions =
            [
                new CommandStateAction("Hibernação", HibernateQuery, CommandStateAction.ParseRegDword,
                    new CommandLine("powercfg", "/h on"), new CommandLine("powercfg", "/h off"), target: false,
                    stateReader: HibernationEnabled),
            ],
        },

        // ═════════════ Etapa 2C · Memória ═════════════
        new Tweak
        {
            Id = "memory.pagefile-auto",
            Consequences = ["Substitui o tamanho do arquivo de paginação que você configurou à mão."],
            Name = "Deixar o Windows gerenciar o arquivo de paginação",
            Description =
                "Volta o arquivo de paginação para o tamanho automático na unidade do Windows. Use se você o desativou " +
                "ou fixou um tamanho e tem travamentos, jogos fechando ou avisos de memória insuficiente.",
            Benefit = "Evita falhas por falta de memória em jogos pesados.",
            Category = TweakCategory.Memory,
            Risk = RiskLevel.Medium, // substitui uma configuração manual do usuário
            Impact = ImpactLevel.Medium,
            RequiresRestart = true,
            Profiles = P(),
            Actions = [new RegistryValueAction(RegHive.LocalMachine, MemoryManagement, "PagingFiles",
                new RegValue(RegKind.MultiString, new[] { @"?:\pagefile.sys" }))],
        },
        new Tweak
        {
            Id = "memory.compression-off",
            Consequences = ["Com menos de 32 GB de RAM o Windows pode usar mais o disco e ficar mais lento."],
            Name = "Desativar a compressão de memória",
            Description =
                "O Windows para de comprimir páginas da RAM para caber mais coisa nela. Só vale a pena com 32 GB ou mais: " +
                "com menos memória, o PC pode passar a usar mais o disco.",
            Benefit = "Libera um pouco de CPU que seria gasto comprimindo e descomprimindo memória.",
            Category = TweakCategory.Memory,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(),
            Actions =
            [
                new CommandStateAction("Compressão de memória", MmAgentQuery, CommandStateAction.ParseBool,
                    new CommandLine("powershell", "-NoProfile -NonInteractive -Command \"Enable-MMAgent -MemoryCompression\""),
                    new CommandLine("powershell", "-NoProfile -NonInteractive -Command \"Disable-MMAgent -MemoryCompression\""),
                    target: false),
            ],
        },

        // ═════════════ Etapa 2D · Serviços ═════════════
        new Tweak
        {
            Id = "services.rarely-used",
            Name = "Desativar serviços raramente usados",
            Description =
                "Desliga serviços que quase ninguém usa em PC doméstico: Fax, Mapas offline, Experiência de varejo (lojas), " +
                "Registro Remoto e Geolocalização de rede Wi-Fi do sensor. Os que não existirem no seu Windows são ignorados.",
            Benefit = "Menos processos em segundo plano e um início do Windows um pouco mais leve.",
            Category = TweakCategory.ProcessesServices,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions =
            [
                new ServiceStartupAction("Fax", ServiceStartMode.Disabled),
                new ServiceStartupAction("MapsBroker", ServiceStartMode.Disabled),
                new ServiceStartupAction("RetailDemo", ServiceStartMode.Disabled),
                new ServiceStartupAction("RemoteRegistry", ServiceStartMode.Disabled),
                new ServiceStartupAction("WMPNetworkSvc", ServiceStartMode.Disabled),
            ],
        },
        new Tweak
        {
            Id = "services.sysmain",
            Consequences = ["Programas podem abrir um pouco mais devagar nas primeiras vezes."],
            Name = "Desativar o SysMain (Superfetch)",
            Description =
                "O SysMain pré-carrega na RAM os programas que você mais usa. Em SSD rápido o ganho dele é pequeno, " +
                "e em alguns PCs causa uso alto de disco logo após ligar.",
            Benefit = "Evita picos de disco e de memória no início; útil se você nota o disco a 100% sem motivo.",
            Category = TweakCategory.ProcessesServices,
            Risk = RiskLevel.Medium, // programas podem abrir um pouco mais devagar num HD
            Impact = ImpactLevel.Medium,
            Profiles = P(),
            Conditions = [TweakCondition.SystemDriveIsSsd], // num HD o SysMain ajuda: lá ele fica
            Actions = [new ServiceStartupAction("SysMain", ServiceStartMode.Disabled)],
        },
        new Tweak
        {
            Id = "services.search-indexer",
            Consequences = ["A busca do Iniciar e do Explorador fica mais lenta e pode não achar o conteúdo dentro de arquivos e e-mails do Outlook."],
            Name = "Desativar a indexação de pesquisa",
            Description =
                "Para o serviço Windows Search, que fica lendo seus arquivos para acelerar buscas. " +
                "A pesquisa continua funcionando, só fica mais lenta (procura na hora).",
            Benefit = "Menos leitura de disco e CPU em segundo plano, principalmente com muitos arquivos.",
            Category = TweakCategory.ProcessesServices,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Medium,
            Profiles = P(),
            Actions = [new ServiceStartupAction("WSearch", ServiceStartMode.Disabled)],
        },
        new Tweak
        {
            Id = "services.print-spooler",
            Consequences = ["Nenhuma impressora funciona, nem \"Imprimir em PDF\"."],
            Name = "Desativar o serviço de impressão",
            Description =
                "Desliga o Spooler de Impressão. Só faça isso se você não usa impressora (nem \"Imprimir em PDF\").",
            Benefit = "Um serviço a menos rodando e menos superfície de ataque.",
            Category = TweakCategory.ProcessesServices,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Actions = [new ServiceStartupAction("Spooler", ServiceStartMode.Disabled)],
        },

        // ═════════════ Etapa 2D · Rede ═════════════
        new Tweak
        {
            Id = "network.throttling",
            Name = "Remover o limite de rede para multimídia",
            Description =
                "Por padrão o Windows limita o tráfego de rede enquanto toca áudio/vídeo e reserva 20% da CPU " +
                "para tarefas de fundo. Este ajuste tira o limite e reduz a reserva para 10%.",
            Benefit = "Download e jogo online mais estáveis enquanto há música ou chamada de voz aberta.",
            Category = TweakCategory.Network,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Medium,
            RequiresRestart = true,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions =
            [
                LmDword(MultimediaProfile, "NetworkThrottlingIndex", 0xFFFFFFFF),
                LmDword(MultimediaProfile, "SystemResponsiveness", 10),
            ],
        },
        new Tweak
        {
            Id = "network.nagle-off",
            Name = "Desativar o algoritmo de Nagle",
            Description =
                "O Windows junta pacotes pequenos antes de enviar, o que economiza banda mas atrasa um pouco cada um. " +
                "Este ajuste manda os pacotes na hora, em todas as placas de rede.",
            Benefit = "Pode reduzir o ping em jogos online que usam TCP (muitos MMOs e alguns competitivos).",
            Category = TweakCategory.Network,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(Gamer),
            Actions =
            [
                new RegistryEachSubkeyAction(RegHive.LocalMachine, DnsServersAction.InterfacesKey, "TcpAckFrequency", RegValue.Dword(1), "Placas de rede"),
                new RegistryEachSubkeyAction(RegHive.LocalMachine, DnsServersAction.InterfacesKey, "TCPNoDelay", RegValue.Dword(1), "Placas de rede"),
            ],
        },
        new Tweak
        {
            Id = "network.dns-cloudflare",
            Consequences = ["Em rede de empresa ou escola, sites e pastas internas podem parar de abrir."],
            Name = "Usar DNS rápido (Cloudflare 1.1.1.1)",
            Description =
                "Troca o DNS das placas conectadas para 1.1.1.1 e 1.0.0.1. Ao reverter, volta exatamente ao DNS que " +
                "estava antes (ou ao automático do roteador). Em redes de empresa/escola, pode impedir o acesso a sites internos.",
            Benefit = "Sites e lojas de jogos podem começar a carregar mais rápido, e o DNS do provedor deixa de ver seus acessos.",
            Category = TweakCategory.Network,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Actions = [new DnsServersAction(["1.1.1.1", "1.0.0.1"], "Cloudflare")],
        },

        // ═════════════ Catálogo extra B · Serviços e privacidade ═════════════
        new Tweak
        {
            Id = "services.error-reporting",
            Consequences = ["A Microsoft e os desenvolvedores deixam de receber relatórios de travamento; soluções automáticas para travamentos param de aparecer."],
            Name = "Desativar o Relatório de Erros do Windows",
            Description =
                "Quando um programa trava, o Windows deixa de gerar o relatório e de enviá-lo à Microsoft. O aviso " +
                "\"o programa parou de funcionar\" continua aparecendo. Também desliga o serviço de mensagens WAP push, " +
                "usado só em PCs gerenciados por empresa ou escola (Intune).",
            Benefit = "Evita o WerFault.exe gastando CPU e disco depois de um travamento, e dá mais privacidade.",
            Category = TweakCategory.ProcessesServices,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Balanced, MaxPerformance, Gamer),
            Actions =
            [
                new ServiceStartupAction("WerSvc", ServiceStartMode.Disabled),
                LmDword(@"SOFTWARE\Microsoft\Windows\Windows Error Reporting", "Disabled", 1),
                new ServiceStartupAction("dmwappushservice", ServiceStartMode.Disabled),
            ],
        },
        new Tweak
        {
            Id = "privacy.app-launch-tracking",
            Name = "Não registrar os apps que você abre",
            Description = "O Windows para de anotar quais programas você abre. A lista \"Mais usados\" do menu Iniciar fica vazia.",
            Benefit = "Mais privacidade. Não muda o desempenho.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Safe, Balanced, MaxPerformance, Gamer),
            Actions = [CuDword(ExplorerAdvanced, "Start_TrackProgs", 0)],
        },
        new Tweak
        {
            Id = "gpu.nvidia-telemetry",
            Name = "Desativar a telemetria da NVIDIA",
            Description =
                "Desliga o serviço \"NVIDIA Telemetry Container\", que envia dados de uso à NVIDIA. O driver e o painel " +
                "da NVIDIA continuam funcionando. Só aparece em drivers antigos que ainda têm esse serviço.",
            Benefit = "Um processo a menos em segundo plano e mais privacidade.",
            Category = TweakCategory.Gpu,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(MaxPerformance, Gamer),
            Conditions = [TweakCondition.HasGpuVendor(GpuVendor.Nvidia), TweakCondition.ServiceExists("NvTelemetryContainer")],
            Actions = [new ServiceStartupAction("NvTelemetryContainer", ServiceStartMode.Disabled)],
        },

        // ═════════════ Catálogo extra C · Visual ═════════════
        new Tweak
        {
            Id = "system.visual-best-performance",
            Name = "Efeitos visuais: melhor desempenho",
            Description =
                "Igual a \"Ajustar para obter um melhor desempenho\" nas Opções de desempenho do Windows: desliga sombras, " +
                "animações de controles, conteúdo da janela ao arrastar e o esmaecer de menus e dicas. " +
                "A suavização das fontes (ClearType) não muda. Vale depois de sair e entrar na conta.",
            Benefit = "Interface mais leve, principalmente com vídeo integrado ou PC mais fraco.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(MaxPerformance),
            Actions =
            [
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", 2),
                new RegistryValueAction(RegHive.CurrentUser, @"Control Panel\Desktop", "UserPreferencesMask",
                    new RegValue(RegKind.Binary, new byte[] { 0x90, 0x12, 0x03, 0x80, 0x10, 0x00, 0x00, 0x00 })),
            ],
        },

        // ═════════════ Catálogo extra D · Energia e periféricos ═════════════
        new Tweak
        {
            Id = "peripherals.usb-selective-suspend",
            Name = "Não suspender dispositivos USB",
            Description =
                "O Windows para de desligar portas USB paradas para economizar energia. Vale no plano de energia ativo, " +
                "com o PC na tomada; na bateria nada muda.",
            Benefit = "Corrige mouse, teclado, headset ou controle USB que \"dorme\", demora a responder ou desconecta.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            Profiles = P(Gamer),
            Actions = [new PowerSettingAction(SubUsb, UsbSelectiveSuspend, 0, "suspensão seletiva de USB desligada")],
        },
        new Tweak
        {
            Id = "network.adapter-power-saving",
            Consequences = ["A internet cai por alguns segundos enquanto a placa reinicia."],
            Name = "Não desligar a placa de rede para economizar",
            Description =
                "Desmarca \"Permitir que o computador desligue este dispositivo para economizar energia\" nas placas de rede " +
                "(Wi-Fi e cabo). O Wake-on-LAN não muda. A placa reinicia por alguns segundos ao aplicar.",
            Benefit = "Corrige internet caindo ou demorando a voltar depois que o PC fica parado.",
            Category = TweakCategory.Network,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            PortableWarning = "Neste notebook, a placa de rede vai gastar um pouco mais de bateria.",
            Actions = [new NetAdapterPowerAction()],
        },

        // ═════════════ Catálogo extra J · Ajustes de risco (fora de todos os perfis) ═════════════
        new Tweak
        {
            Id = "security.memory-integrity-off",
            Name = "Desativar a Integridade de Memória (HVCI) e o VBS",
            Description =
                "Desliga a \"Integridade de Memória\" (Segurança do Windows → Segurança do dispositivo → Isolamento de núcleo) " +
                "e a segurança baseada em virtualização. Reduz a proteção do Windows contra drivers maliciosos.",
            Benefit = "Alguns % de FPS em CPUs mais antigas (antes de Intel 11ª geração e Ryzen 3000). Em CPU nova o ganho é quase nulo.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.High,
            Impact = ImpactLevel.Medium,
            RequiresRestart = true,
            Profiles = P(),
            Consequences =
            [
                "O Windows fica mais exposto a drivers maliciosos e a ataques que usam falhas de driver.",
                "A Segurança do Windows mostra um alerta amarelo.",
                "Uma atualização do Windows pode religar sozinha (o item volta a aparecer como não aplicado).",
            ],
            Conditions = [TweakCondition.DeviceGuardChangeable],
            Actions =
            [
                LmDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard\Scenarios\HypervisorEnforcedCodeIntegrity", "Enabled", 0),
                LmDword(@"SYSTEM\CurrentControlSet\Control\DeviceGuard", "EnableVirtualizationBasedSecurity", 0),
            ],
        },
        new Tweak
        {
            Id = "security.credential-guard-off",
            Name = "Desativar o Credential Guard",
            Description = "Desliga a proteção que isola senhas e tokens de login da memória do Windows. Não aumenta o FPS: só libera o recurso.",
            Benefit = "Quase nenhum ganho de desempenho. Útil só se o Credential Guard atrapalha algum programa (ex.: VPN ou Wi-Fi corporativo antigo).",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.High,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(),
            Consequences =
            [
                "Senhas e tokens de login ficam menos protegidos contra programas que roubam credenciais da memória.",
                "Em PC de empresa, pode violar a política de segurança.",
            ],
            Conditions = [TweakCondition.CredentialGuardChangeable],
            Actions = [LmDword(@"SYSTEM\CurrentControlSet\Control\Lsa", "LsaCfgFlags", 0)],
        },
        new Tweak
        {
            Id = "security.hypervisor-off",
            Name = "Não carregar o hipervisor do Windows",
            Description = "O Windows deixa de iniciar o hipervisor no boot (bcdedit hypervisorlaunchtype off). Desliga junto o VBS e a Integridade de Memória.",
            Benefit = "Pode dar alguns % de FPS em CPUs antigas e reduzir a latência em alguns jogos.",
            Category = TweakCategory.Cpu,
            Risk = RiskLevel.High,
            Impact = ImpactLevel.Medium,
            RequiresRestart = true,
            Profiles = P(),
            Consequences =
            [
                "WSL2, Hyper-V, Windows Sandbox, Docker Desktop e emuladores Android baseados em Hyper-V param de funcionar.",
                "Desliga também o VBS e a Integridade de Memória.",
                "Se o disco usa BitLocker, tenha a chave de recuperação à mão por precaução.",
            ],
            Actions = [new BcdSettingAction("hypervisorlaunchtype", "Off", "hipervisor desligado")],
        },
        new Tweak
        {
            Id = "services.biometrics-off",
            Name = "Desativar o serviço de Biometria",
            Description = "Desliga o serviço que lê digital e rosto (Windows Hello).",
            Benefit = "Praticamente nenhum ganho de desempenho: o serviço só roda quando é usado.",
            Category = TweakCategory.ProcessesServices,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Consequences =
            [
                "O Windows Hello por digital ou rosto para de funcionar: você vai precisar do PIN ou da senha.",
                "Ganho de desempenho praticamente nenhum (o serviço só roda quando é usado).",
            ],
            Actions = [new ServiceStartupAction("WbioSrvc", ServiceStartMode.Disabled)],
        },
        new Tweak
        {
            Id = "gaming.xbox-services-off",
            Name = "Desativar os serviços Xbox",
            Description = "Desliga login Xbox, saves na nuvem, rede Xbox e o serviço dos controles Xbox.",
            Benefit = "Praticamente nenhum ganho: esses serviços só rodam quando algo os usa.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Consequences =
            [
                "Jogos do Game Pass e jogos que usam conta Xbox não entram na conta.",
                "Saves na nuvem do Xbox param de sincronizar.",
                "Controles e acessórios Xbox (atualização de firmware, botões extras) podem parar de funcionar.",
                "Ganho de desempenho praticamente nenhum: esses serviços só rodam quando algo os usa.",
            ],
            Actions =
            [
                new ServiceStartupAction("XblAuthManager", ServiceStartMode.Disabled),
                new ServiceStartupAction("XblGameSave", ServiceStartMode.Disabled),
                new ServiceStartupAction("XboxNetApiSvc", ServiceStartMode.Disabled),
                new ServiceStartupAction("XboxGipSvc", ServiceStartMode.Disabled),
            ],
        },
        new Tweak
        {
            Id = "privacy.cortana-off",
            Name = "Desativar a Cortana",
            Description = "Desliga a Cortana pela política do Windows. Só tem efeito no Windows 10.",
            Benefit = "Um assistente a menos em segundo plano e mais privacidade.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            ManualOnly = true,
            Profiles = P(),
            Consequences = ["A Cortana para de responder e some da barra de tarefas."],
            Conditions = [TweakCondition.MaxWindowsBuild(21999)],
            Actions =
            [
                LmDword(@"SOFTWARE\Policies\Microsoft\Windows\Windows Search", "AllowCortana", 0),
                CuDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", 0),
            ],
        },
        new Tweak
        {
            Id = "system.taskbar-search-hidden",
            Name = "Esconder a pesquisa da barra de tarefas",
            Description = "Tira a caixa (ou o ícone) de pesquisa da barra de tarefas. A pesquisa continua: aperte a tecla Windows e digite.",
            Benefit = "Só visual: mais espaço na barra. Não muda o desempenho.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Low,
            Impact = ImpactLevel.Low,
            ManualOnly = true,
            Profiles = P(),
            Actions = [CuDword(@"Software\Microsoft\Windows\CurrentVersion\Search", "SearchboxTaskbarMode", 0)],
        },
        new Tweak
        {
            Id = "disk.prefetcher-off",
            Name = "Desativar o Prefetch",
            Description = "O Windows para de pré-carregar arquivos que costuma usar no boot e ao abrir programas.",
            Benefit = "Só faz sentido para testar se o disco fica a 100% por causa disso.",
            Category = TweakCategory.Disk,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(),
            Consequences =
            [
                "O Windows e os programas tendem a abrir mais devagar, principalmente em HD.",
                "Só faz sentido para testar se o disco fica a 100% por causa disso.",
            ],
            ContextWarnings = [new(c => c.Profile.SystemDriveType == Hardware.DiskMediaType.Hdd, "Seu Windows está num HD: lá o Prefetch é o que mais ajuda. Desligar costuma piorar.")],
            Actions = [LmDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters", "EnablePrefetcher", 0)],
        },
        new Tweak
        {
            Id = "compat.pca-off",
            Name = "Desativar o Assistente de Compatibilidade de Programas",
            Description = "Desliga o serviço que observa programas antigos que falham e o inventário de compatibilidade.",
            Benefit = "Um serviço a menos vigiando cada programa que abre.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Consequences = ["O Windows para de oferecer \"executar com configurações recomendadas\" quando um programa antigo falha ao instalar ou abrir."],
            Actions =
            [
                new ServiceStartupAction("PcaSvc", ServiceStartMode.Disabled),
                LmDword(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisablePCA", 1),
                LmDword(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableInventory", 1),
            ],
        },
        new Tweak
        {
            Id = "compat.engine-off",
            Name = "Desativar o motor de compatibilidade de aplicativos",
            Description = "Desliga as correções automáticas que o Windows aplica em programas antigos.",
            Benefit = "Programas abrem sem passar pelo motor de compatibilidade (ganho muito pequeno).",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.High,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(),
            Consequences =
            [
                "Programas e jogos antigos que dependem das correções de compatibilidade do Windows podem parar de abrir ou travar.",
                "Configurações feitas em Propriedades → Compatibilidade deixam de valer.",
            ],
            Actions = [LmDword(@"SOFTWARE\Policies\Microsoft\Windows\AppCompat", "DisableEngine", 1)],
        },
        new Tweak
        {
            Id = "update.block-internet-locations",
            Name = "Impedir o Windows Update de usar servidores da internet",
            Description =
                "Política para PCs que recebem atualizações de um servidor da empresa (WSUS). Não é telemetria e não acelera o PC.",
            Benefit = "Num PC com WSUS, garante que ele só atualiza pelo servidor da empresa.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.High,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Consequences = ["A Microsoft Store e alguns downloads de recursos do Windows podem parar de funcionar."],
            ContextWarnings =
            [
                new(c => c.Registry.GetValue(Abstractions.RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "WUServer") is null,
                    "Este PC não usa WSUS: este ajuste quase não muda nada e pode quebrar a Store."),
            ],
            Actions = [LmDword(@"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "DoNotConnectToWindowsUpdateInternetLocations", 1)],
        },

        // ═════════════ Catálogo extra K · Permissões de câmera e microfone (usadas pelos reparos) ═════════════
        new Tweak
        {
            Id = "privacy.camera-allow-apps",
            Name = "Liberar a câmera para os apps (sua conta)",
            Description = "Marca \"Permitir que os apps acessem a câmera\" nas configurações de privacidade, para apps da Store e programas comuns.",
            Benefit = "Resolve câmera bloqueado pela privacidade do Windows em apps como Discord, Teams e navegador.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            ManualOnly = true,
            Profiles = P(),
            Consequences = ["Todos os apps passam a poder usar sua câmera: revise em Configurações → Privacidade."],
            Actions =
            [
                new RegistryValueAction(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "Value", RegValue.Str("Allow")),
                new RegistryValueAction(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged", "Value", RegValue.Str("Allow")),
            ],
        },
        new Tweak
        {
            Id = "privacy.camera-allow-apps-all-users",
            Name = "Liberar a câmera para os apps (todos os usuários)",
            Description = "Libera o acesso aa câmera na configuração do PC inteiro, que vale para todas as contas.",
            Benefit = "Resolve câmera bloqueado para todas as contas deste PC.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            ManualOnly = true,
            Profiles = P(),
            Consequences = ["Os apps de todas as contas deste PC passam a poder usar a câmera."],
            Actions =
            [
                new RegistryValueAction(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam", "Value", RegValue.Str("Allow")),
                new RegistryValueAction(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam\NonPackaged", "Value", RegValue.Str("Allow")),
            ],
        },
        new Tweak
        {
            Id = "privacy.camera-remove-policy",
            Name = "Remover bloqueios de política da câmera",
            Description = "Apaga as políticas que bloqueiam a câmera (normalmente colocadas por empresa, escola ou programas de \"privacidade\"). Os valores ficam guardados para o Desfazer.",
            Benefit = "Resolve câmera bloqueado por política, quando o Windows mostra \"gerenciado pela sua organização\".",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.High,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Consequences =
            [
                "Em PC de empresa ou escola, isso desfaz uma regra do administrador, e ela pode voltar na próxima sincronização.",
                "Pode violar a política de uso do PC da empresa ou escola.",
            ],
            Actions =
            [
                new RegistryDeleteValueAction(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Camera", "AllowCamera"),
                new RegistryDeleteValueAction(RegHive.CurrentUser, @"Software\Policies\Microsoft\Camera", "AllowCamera"),
                new RegistryDeleteValueAction(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessCamera"),
                new RegistryDeleteValueAction(RegHive.CurrentUser, @"Software\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessCamera"),
            ],
        },
        new Tweak
        {
            Id = "privacy.microphone-allow-apps",
            Name = "Liberar o microfone para os apps (sua conta)",
            Description = "Marca \"Permitir que os apps acessem o microfone\" nas configurações de privacidade, para apps da Store e programas comuns.",
            Benefit = "Resolve microfone bloqueado pela privacidade do Windows em apps como Discord, Teams e navegador.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            ManualOnly = true,
            Profiles = P(),
            Consequences = ["Todos os apps passam a poder usar seu microfone: revise em Configurações → Privacidade."],
            Actions =
            [
                new RegistryValueAction(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", "Value", RegValue.Str("Allow")),
                new RegistryValueAction(RegHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged", "Value", RegValue.Str("Allow")),
            ],
        },
        new Tweak
        {
            Id = "privacy.microphone-allow-apps-all-users",
            Name = "Liberar o microfone para os apps (todos os usuários)",
            Description = "Libera o acesso ao microfone na configuração do PC inteiro, que vale para todas as contas.",
            Benefit = "Resolve microfone bloqueado para todas as contas deste PC.",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            ManualOnly = true,
            Profiles = P(),
            Consequences = ["Os apps de todas as contas deste PC passam a poder usar o microfone."],
            Actions =
            [
                new RegistryValueAction(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone", "Value", RegValue.Str("Allow")),
                new RegistryValueAction(RegHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone\NonPackaged", "Value", RegValue.Str("Allow")),
            ],
        },
        new Tweak
        {
            Id = "privacy.microphone-remove-policy",
            Name = "Remover bloqueios de política do microfone",
            Description = "Apaga as políticas que bloqueiam o microfone (normalmente colocadas por empresa, escola ou programas de \"privacidade\"). Os valores ficam guardados para o Desfazer.",
            Benefit = "Resolve microfone bloqueado por política, quando o Windows mostra \"gerenciado pela sua organização\".",
            Category = TweakCategory.SystemPrivacy,
            Risk = RiskLevel.High,
            Impact = ImpactLevel.Low,
            Profiles = P(),
            Consequences =
            [
                "Em PC de empresa ou escola, isso desfaz uma regra do administrador, e ela pode voltar na próxima sincronização.",
                "Pode violar a política de uso do PC da empresa ou escola.",
            ],
            Actions =
            [
                new RegistryDeleteValueAction(RegHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessMicrophone"),
                new RegistryDeleteValueAction(RegHive.CurrentUser, @"Software\Policies\Microsoft\Windows\AppPrivacy", "LetAppsAccessMicrophone"),
            ],
        },

        // ═════════════ Catálogo extra L · Timer global (Windows 11) ═════════════
        new Tweak
        {
            Id = "gaming.global-timer-resolution",
            Name = "Permitir timer de alta resolução para o sistema todo",
            Description =
                "No Windows 11, a resolução de timer que um programa pede só vale para ele mesmo. Este ajuste volta ao comportamento " +
                "antigo: quando o Modo Gamer pede 0,5 ms, vale para o jogo também.",
            Benefit = "Necessário para o \"Timer de alta resolução\" do Modo Gamer ter efeito no Windows 11.",
            Category = TweakCategory.Gaming,
            Risk = RiskLevel.Medium,
            Impact = ImpactLevel.Low,
            RequiresRestart = true,
            Profiles = P(),
            Consequences =
            [
                "Mais consumo de energia e CPU um pouco mais ativa enquanto algum programa pede timer alto; em notebook a bateria dura menos.",
                "O ganho de latência é pequeno e varia por jogo.",
            ],
            Conditions = [TweakCondition.MinWindowsBuild(22000)],
            Actions = [LmDword(@"SYSTEM\CurrentControlSet\Control\Session Manager\kernel", "GlobalTimerResolutionRequests", 1)],
        },
    ];
}
