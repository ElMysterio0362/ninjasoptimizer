using System.Text.RegularExpressions;

namespace NinjaOptimizer.Core.Debloat;

public enum AppVerdict { Safe, Caution, NotRecommended, Unknown }

/// <summary>App da Store que o NinjaOptimizer conhece: o que é e o que acontece se remover.</summary>
public sealed record AppEntry(string[] Names, string FriendlyName, AppVerdict Verdict, string[] Consequences, string? StoreId = null);

public static partial class AppCatalog
{
    /// <summary>Ids da Store anotados na análise e não conferidos um a um; se o winget falhar, o desfazer abre a página na Store.</summary>
    public static IReadOnlyList<AppEntry> Known { get; } =
    [
        new(["Microsoft.BingNews"], "Notícias", AppVerdict.Safe, ["Some o app Notícias. O widget de notícias da barra continua."], "9WZDNCRFHVFW"),
        new(["Microsoft.BingWeather"], "Clima", AppVerdict.Safe, ["Some o app Clima."], "9WZDNCRFJ3Q2"),
        new(["Microsoft.GetHelp"], "Obter Ajuda", AppVerdict.Safe, ["Alguns botões \"Obter ajuda\" do Windows param de abrir."]),
        new(["Microsoft.Getstarted"], "Dicas", AppVerdict.Safe, ["Some o app de dicas do Windows."]),
        new(["Microsoft.MicrosoftOfficeHub"], "Microsoft 365 (Office)", AppVerdict.Safe, ["Some o atalho do Office. O Office instalado (Word, Excel…) não é afetado."], "9WZDNCRD29V9"),
        new(["Microsoft.MicrosoftSolitaireCollection"], "Paciência", AppVerdict.Safe, ["Perde o jogo e o progresso salvo no PC."], "9WZDNCRFHWD2"),
        new(["Microsoft.WindowsFeedbackHub"], "Hub de Comentários", AppVerdict.Safe, ["Não dá mais para enviar comentários à Microsoft pelo app."], "9NBLGGH4R32N"),
        new(["Microsoft.3DBuilder", "Microsoft.Microsoft3DViewer"], "3D Builder / Visualizador 3D", AppVerdict.Safe, ["Arquivos 3D (.3mf, .stl) deixam de abrir com esses apps."]),
        new(["Microsoft.People"], "Pessoas", AppVerdict.Safe, ["Some o app Pessoas (contatos do Email antigo)."]),
        new(["Microsoft.Messaging"], "Mensagens", AppVerdict.Safe, ["App antigo, sem uso no Windows atual."]),
        new(["Microsoft.WindowsMaps"], "Mapas", AppVerdict.Safe, ["Some o app Mapas e os mapas offline baixados."], "9WZDNCRDTBVB"),
        new(["Clipchamp.Clipchamp"], "Clipchamp", AppVerdict.Safe, ["Some o editor de vídeo."], "9P1J8S7CCWWT"),
        new(["Microsoft.ZuneVideo"], "Filmes e TV", AppVerdict.Caution, ["Vídeos comprados na Microsoft podem não abrir; arquivos de vídeo passam a abrir em outro player."], "9WZDNCRFJ3P2"),
        new(["Microsoft.WindowsAlarms"], "Relógio (Alarmes)", AppVerdict.Caution, ["Alarmes, timer e Sessões de foco param de funcionar."], "9WZDNCRFJ3PR"),
        new(["microsoft.windowscommunicationsapps"], "Email e Calendário", AppVerdict.Caution,
            ["As contas de e-mail e calendário configuradas nele somem do PC (os e-mails continuam no servidor).", "Lembretes do calendário param."], "9WZDNCRFHVQM"),
        new(["Microsoft.YourPhone"], "Vincular ao Celular", AppVerdict.Caution, ["Para de mostrar notificações, fotos e mensagens do celular no PC."], "9NMPJ99VJBWV"),
        new(["MicrosoftCorporationII.QuickAssist"], "Assistência Rápida", AppVerdict.Caution,
            ["Não dá mais para receber ou dar suporte remoto por ele (golpistas usam muito: remover também protege)."], "9P7BP5VNWKX5"),
        new(["Microsoft.549981C3F5F10"], "Cortana", AppVerdict.Caution, ["Some a Cortana (já descontinuada no Windows 11)."], "9NFFX4SZZ23L"),
        new(["Microsoft.ZuneMusic"], "Media Player (Groove no Windows 10)", AppVerdict.NotRecommended,
            ["No Windows 11 remove o Media Player: músicas e vídeos passam a abrir em outro programa ou não abrem."], "9WZDNCRFJ3PT"),
        new(["Microsoft.WindowsCamera"], "Câmera", AppVerdict.NotRecommended,
            ["O app Câmera some; a webcam continua funcionando em outros apps (Teams, Discord, navegador)."], "9WZDNCRFJBBG"),
        new(["Microsoft.Windows.Photos"], "Fotos", AppVerdict.NotRecommended, ["Imagens deixam de abrir com dois cliques até você escolher outro programa."], "9WZDNCRFJBH4"),
        new(["Microsoft.GamingApp", "Microsoft.XboxApp"], "Xbox", AppVerdict.NotRecommended, ["Jogos do Game Pass para PC deixam de abrir e atualizar."], "9MV0B5HZVK9Z"),
        new(["Microsoft.XboxGamingOverlay"], "Xbox Game Bar", AppVerdict.NotRecommended,
            ["Win+G, gravação de tela, FPS da Game Bar e widgets somem.", "Alguns jogos mostram erro ao tentar abrir a Game Bar."], "9NZKPSTSNW4P"),
        new(["Microsoft.XboxGameOverlay", "Microsoft.XboxSpeechToTextOverlay"], "Componentes da Game Bar", AppVerdict.NotRecommended,
            ["Partes da Game Bar e da conversão de voz em texto param."]),
        new(["Microsoft.XboxIdentityProvider", "Microsoft.Xbox.TCUI"], "Login Xbox", AppVerdict.NotRecommended,
            ["Jogos que usam conta Xbox (Minecraft, Forza, Halo, Sea of Thieves, jogos da Microsoft na Steam) não conseguem entrar na conta."]),
    ];

    public static readonly string[] UnknownConsequences =
        ["O NinjaOptimizer não conhece este app. Remover pode quebrar o programa que depende dele."];

    public static readonly string[] ForNewAccountsConsequences =
        ["Contas criadas depois neste PC não terão o app, e reinstalar pode exigir a Microsoft Store."];

    /// <summary>Nunca listados nem removidos. "*" no fim = prefixo; "*" no começo = sufixo.</summary>
    public static readonly string[] Protected =
    [
        "Microsoft.WindowsStore", "Microsoft.StorePurchaseApp", "Microsoft.DesktopAppInstaller", "Microsoft.SecHealthUI",
        "Microsoft.Windows.ShellExperienceHost", "Microsoft.Windows.StartMenuExperienceHost", "MicrosoftWindows.Client.*",
        "Microsoft.AAD.BrokerPlugin", "Microsoft.AccountsControl", "Microsoft.Windows.CloudExperienceHost", "Microsoft.LockApp",
        "Microsoft.WindowsTerminal", "Microsoft.WindowsNotepad", "Microsoft.Paint", "Microsoft.ScreenSketch", "Microsoft.WindowsCalculator",
        "*Extension", "Microsoft.HEVCVideoExtension", "Microsoft.VCLibs.*", "Microsoft.UI.Xaml.*", "Microsoft.NET.Native.*",
        "Microsoft.WindowsAppRuntime.*", "Microsoft.Services.Store.Engagement", "Microsoft.Windows.*",
    ];

    public static bool IsProtected(string name) => Protected.Any(p =>
        p.EndsWith('*') ? name.StartsWith(p[..^1], StringComparison.OrdinalIgnoreCase)
        : p.StartsWith('*') ? name.EndsWith(p[1..], StringComparison.OrdinalIgnoreCase)
        : name.Equals(p, StringComparison.OrdinalIgnoreCase))
        && !Known.Any(k => k.Names.Contains(name, StringComparer.OrdinalIgnoreCase)); // Fotos é Microsoft.Windows.*, mas está no catálogo

    public static AppEntry? Find(string name) => Known.FirstOrDefault(k => k.Names.Contains(name, StringComparer.OrdinalIgnoreCase));

    [GeneratedRegex(@"^[A-Za-z0-9._-]+$")]
    private static partial Regex SafeName();
    [GeneratedRegex(@"^[A-Z0-9]{12}$")]
    private static partial Regex StoreIdRegex();

    /// <summary>Só esses caracteres entram numa linha de PowerShell.</summary>
    public static bool IsValidPackageName(string? s) => s is { Length: > 0 and < 256 } && SafeName().IsMatch(s);
    public static bool IsValidStoreId(string? s) => s is not null && StoreIdRegex().IsMatch(s);
}
