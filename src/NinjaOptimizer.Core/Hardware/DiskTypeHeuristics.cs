using System.Text.RegularExpressions;

namespace NinjaOptimizer.Core.Hardware;

/// <summary>
/// Muitos discos SATA antigos e controladoras não informam o MediaType ao Windows ("Unspecified").
/// Inferimos pela rotação informada e por padrões de modelo dos fabricantes.
/// </summary>
public static partial class DiskTypeHeuristics
{
    // Hitachi/HGST (HTS, HDS, HDT, HUA), Seagate (ST + dígitos), WD (WD + dígitos), Toshiba (MQ, DT, MG, HDW), Samsung HD (HD + dígitos), Maxtor.
    [GeneratedRegex(@"^(?:(?:HITACHI|HGST|SEAGATE)\s+)?(HTS|HDS|HDT|HUA|HGST|ST\d|WDC\s?WD\d|WD\d{3,}|TOSHIBA\s(MQ|DT|MG|HDW|MK)|HDW|MQ0|DT0|SAMSUNG\sHD\d|HD\d{3}|MAXTOR)", RegexOptions.IgnoreCase)]
    private static partial Regex HddModel();

    [GeneratedRegex(@"\b(SSD|NVME|EVO|PRO\s?\d|SN\d{3}|MZ-|SA400|SNV|SNVS|KC\d{3}|A400|MX\d{3}|BX\d{3}|P\d SSD|CT\d+(MX|BX|P))", RegexOptions.IgnoreCase)]
    private static partial Regex SsdModel();

    public static DiskMediaType Infer(string model, DiskMediaType reported, string busType, uint spindleSpeed)
    {
        if (reported != DiskMediaType.Unknown) return reported;
        if (busType == "NVMe") return DiskMediaType.Ssd;
        // SpindleSpeed: 0 = não gira (SSD); 0xFFFFFFFF = desconhecido.
        if (spindleSpeed is > 0 and < uint.MaxValue) return DiskMediaType.Hdd;
        var m = (model ?? "").Trim();
        if (SsdModel().IsMatch(m)) return DiskMediaType.Ssd;
        if (HddModel().IsMatch(m)) return DiskMediaType.Hdd;
        if (spindleSpeed == 0) return DiskMediaType.Ssd;
        return DiskMediaType.Unknown;
    }
}
