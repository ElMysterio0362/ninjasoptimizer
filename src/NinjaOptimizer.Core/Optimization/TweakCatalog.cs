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
    ];
}
