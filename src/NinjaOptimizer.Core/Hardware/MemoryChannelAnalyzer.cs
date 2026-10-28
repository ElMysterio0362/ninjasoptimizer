using System.Text.RegularExpressions;

namespace NinjaOptimizer.Core.Hardware;

/// <summary>
/// O Windows não expõe "canais" de memória diretamente. Inferimos pelos rótulos SMBIOS
/// dos slots (DeviceLocator/BankLabel), que seguem padrões dos fabricantes:
/// "ChannelA-DIMM0", "DIMM_A1", "A1_DIMM0", "P0 CHANNEL A", "BANK 0"...
/// </summary>
public static partial class MemoryChannelAnalyzer
{
    [GeneratedRegex(@"CHANNEL\s*([A-H])", RegexOptions.IgnoreCase)]
    private static partial Regex ChannelWord();

    [GeneratedRegex(@"(?:DIMM|CH)[\s_\-]?([A-H])\d", RegexOptions.IgnoreCase)]
    private static partial Regex DimmLetter();

    [GeneratedRegex(@"^([A-H])\d(?:[_\-\s]|$)", RegexOptions.IgnoreCase)]
    private static partial Regex LeadingLetter();

    [GeneratedRegex(@"BANK\s*(\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex BankNumber();

    /// <summary>Retorna o identificador do canal do módulo, ou null se não der para inferir.</summary>
    public static string? ChannelOf(string slot, string bank)
    {
        foreach (var text in new[] { bank, slot })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = ChannelWord().Match(text);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        foreach (var text in new[] { slot, bank })
        {
            if (string.IsNullOrWhiteSpace(text)) continue;
            var m = DimmLetter().Match(text);
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
            m = LeadingLetter().Match(text.Trim());
            if (m.Success) return m.Groups[1].Value.ToUpperInvariant();
        }
        // "BANK 0/1/2/3": em muitas placas, bancos pares/ímpares = canais distintos.
        var b = BankNumber().Match(bank ?? "");
        if (b.Success) return (int.Parse(b.Groups[1].Value) % 2 == 0) ? "A" : "B";
        return null;
    }

    public static (int Channels, string Mode) Analyze(IReadOnlyList<MemoryModule> modules)
    {
        if (modules.Count == 0) return (0, "Desconhecido");
        var channels = modules.Select(m => ChannelOf(m.Slot, m.Bank)).ToList();
        int count;
        if (channels.All(c => c is not null))
            count = channels.Distinct().Count();
        else
            count = modules.Count >= 2 ? 2 : 1; // heurística: 2+ módulos normalmente ficam em dual channel

        var mode = count switch
        {
            1 => "Single channel",
            2 => "Dual channel",
            3 => "Triple channel",
            4 => "Quad channel",
            _ => $"{count} canais",
        };
        if (channels.Any(c => c is null)) mode += " (estimado)";
        return (count, mode);
    }

    /// <summary>Converte SMBIOSMemoryType (Win32_PhysicalMemory) em texto.</summary>
    public static string MemoryTypeName(int smbiosType) => smbiosType switch
    {
        20 => "DDR",
        21 => "DDR2",
        24 => "DDR3",
        26 => "DDR4",
        34 => "DDR5",
        30 => "LPDDR4",
        35 => "LPDDR5",
        _ => "DDR",
    };
}
