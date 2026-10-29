using NinjaOptimizer.Core.Abstractions;
using NinjaOptimizer.Core.Hardware;

namespace NinjaOptimizer.Core.Optimization;

public enum GpuVendor { Nvidia, Amd, Intel }

/// <summary>
/// Resumo do PC usado pelas condições dos ajustes. Valores nulos = ainda não lido ou não foi possível saber.
/// </summary>
public sealed record SystemProfile(
    bool Loaded,
    DiskMediaType SystemDriveType,
    bool? IsPortable,
    IReadOnlySet<GpuVendor>? GpuVendors,
    int? WindowsBuild)
{
    public static SystemProfile NotLoaded { get; } = new(false, DiskMediaType.Unknown, null, null, null);

    /// <summary>PCI Vendor ID (de "PCI\VEN_10DE&amp;DEV_…") → fabricante.</summary>
    public static GpuVendor? VendorFromPnpId(string pnpDeviceId)
    {
        var i = pnpDeviceId.IndexOf("VEN_", StringComparison.OrdinalIgnoreCase);
        if (i < 0 || pnpDeviceId.Length < i + 8) return null;
        return pnpDeviceId.Substring(i + 4, 4).ToUpperInvariant() switch
        {
            "10DE" => GpuVendor.Nvidia,
            "1002" => GpuVendor.Amd,
            "8086" => GpuVendor.Intel,
            _ => null,
        };
    }

    /// <summary>Tipos de gabinete portáteis do Win32_SystemEnclosure.ChassisTypes.</summary>
    public static readonly IReadOnlySet<int> PortableChassis = new HashSet<int> { 8, 9, 10, 11, 12, 14, 18, 21, 30, 31, 32 };

    public static bool IsPortableChassis(IEnumerable<int> chassisTypes, bool hasBattery) =>
        hasBattery || chassisTypes.Any(PortableChassis.Contains);
}

public sealed record ConditionResult(bool Applicable, string Reason)
{
    public static ConditionResult Ok { get; } = new(true, "");
}

/// <summary>Condição para um ajuste fazer sentido neste PC (ex.: só em SSD, só em notebook).</summary>
public abstract class TweakCondition
{
    public abstract ConditionResult Check(SystemContext ctx);

    protected static ConditionResult No(string reason) => new(false, reason);
    protected const string Checking = "Verificando o hardware do PC…";

    public static TweakCondition SystemDriveIsSsd { get; } = new SsdCondition();
    public static TweakCondition IsDesktop { get; } = new ChassisCondition(portable: false);
    public static TweakCondition IsPortable { get; } = new ChassisCondition(portable: true);
    public static TweakCondition HasGpuVendor(GpuVendor vendor) => new GpuVendorCondition(vendor);
    public static TweakCondition ServiceExists(string name) => new ServiceCondition(name);
    public static TweakCondition MinWindowsBuild(int build) => new BuildCondition(build);

    private sealed class SsdCondition : TweakCondition
    {
        public override ConditionResult Check(SystemContext ctx) => ctx.Profile switch
        {
            { Loaded: false } => No(Checking),
            { SystemDriveType: DiskMediaType.Ssd or DiskMediaType.Scm } => ConditionResult.Ok,
            { SystemDriveType: DiskMediaType.Hdd } => No("Só para SSD — o Windows está num HD."),
            _ => No("Só para SSD — não foi possível confirmar o tipo do disco do Windows."),
        };
    }

    private sealed class ChassisCondition(bool portable) : TweakCondition
    {
        public override ConditionResult Check(SystemContext ctx)
        {
            if (!ctx.Profile.Loaded) return No(Checking);
            if (ctx.Profile.IsPortable is not { } p) return No("Não foi possível confirmar se o PC é notebook ou desktop.");
            if (p == portable) return ConditionResult.Ok;
            return No(portable ? "Só para notebook." : "Só para desktop — este PC é um notebook.");
        }
    }

    private sealed class GpuVendorCondition(GpuVendor vendor) : TweakCondition
    {
        private string Name => vendor switch { GpuVendor.Nvidia => "NVIDIA", GpuVendor.Amd => "AMD", _ => "Intel" };

        public override ConditionResult Check(SystemContext ctx)
        {
            if (!ctx.Profile.Loaded) return No(Checking);
            if (ctx.Profile.GpuVendors is not { } v) return No($"Só para placa de vídeo {Name} — não foi possível confirmar a placa.");
            return v.Contains(vendor) ? ConditionResult.Ok : No($"Só para placa de vídeo {Name}.");
        }
    }

    private sealed class ServiceCondition(string name) : TweakCondition
    {
        public override ConditionResult Check(SystemContext ctx)
        {
            try { return ctx.Services.Exists(name) ? ConditionResult.Ok : No($"O serviço {name} não existe neste Windows."); }
            catch (Exception) { return No($"Não foi possível confirmar se o serviço {name} existe."); }
        }
    }

    private sealed class BuildCondition(int build) : TweakCondition
    {
        public override ConditionResult Check(SystemContext ctx)
        {
            if (!ctx.Profile.Loaded) return No(Checking);
            if (ctx.Profile.WindowsBuild is not { } b) return No("Não foi possível confirmar a versão do Windows.");
            return b >= build ? ConditionResult.Ok : No($"Precisa do Windows build {build} ou mais novo.");
        }
    }
}
