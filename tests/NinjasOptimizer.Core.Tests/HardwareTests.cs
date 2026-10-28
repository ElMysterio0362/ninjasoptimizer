using NinjasOptimizer.Core.Abstractions;
using NinjasOptimizer.Core.Hardware;
using NinjasOptimizer.Core.Optimization.Actions;

namespace NinjasOptimizer.Core.Tests;

public sealed class HardwareTests
{
    [Theory]
    [InlineData("ChannelA-DIMM0", "BANK 0", "A")]
    [InlineData("ChannelB-DIMM1", "BANK 2", "B")]
    [InlineData("DIMM_A1", "", "A")]
    [InlineData("DIMM_B2", "", "B")]
    [InlineData("A1_DIMM0", "P0 CHANNEL A", "A")]
    [InlineData("DIMM 1", "P0 CHANNEL B", "B")]
    [InlineData("DIMM 0", "BANK 1", "B")]
    [InlineData("Controller0-ChannelA", "BANK 0", "A")]
    public void Infers_channel_from_smbios_labels(string slot, string bank, string expected) =>
        Assert.Equal(expected, MemoryChannelAnalyzer.ChannelOf(slot, bank));

    private static MemoryModule M(string slot, string bank) =>
        new(slot, bank, 8UL << 30, 3200, 3200, "X", "Y", "DDR4");

    [Fact]
    public void Two_modules_same_channel_is_single_channel()
    {
        var (count, mode) = MemoryChannelAnalyzer.Analyze([M("ChannelA-DIMM0", ""), M("ChannelA-DIMM1", "")]);
        Assert.Equal(1, count);
        Assert.Equal("Single channel", mode);
    }

    [Fact]
    public void Two_modules_different_channels_is_dual_channel()
    {
        var (count, mode) = MemoryChannelAnalyzer.Analyze([M("DIMM_A2", ""), M("DIMM_B2", "")]);
        Assert.Equal(2, count);
        Assert.Equal("Dual channel", mode);
    }

    [Fact]
    public void Unknown_labels_fall_back_to_estimate()
    {
        var (count, mode) = MemoryChannelAnalyzer.Analyze([M("XMM1", ""), M("XMM2", "")]);
        Assert.Equal(2, count);
        Assert.Contains("estimado", mode);
    }

    [Fact]
    public void Parses_powercfg_guid_in_any_language()
    {
        Assert.Equal("381b4222-f694-41f0-9685-ff5bb260df2e",
            PowerPlanAction.ParseGuid("Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)"));
        Assert.Equal("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c",
            PowerPlanAction.ParseGuid("GUID do Esquema de Energia: 8C5E7FDA-E8BF-4A96-9A85-A6E23A8C635C  (Alto desempenho)"));
    }

    [Theory]
    [InlineData(RegKind.DWord, "4294967295")]
    [InlineData(RegKind.String, "olá")]
    [InlineData(RegKind.MultiString, "a\u0000b")]
    [InlineData(RegKind.Binary, "AQID")]
    public void RegValue_roundtrips(RegKind kind, string raw) =>
        Assert.Equal(raw, RegValue.Deserialize(kind, raw).Serialize());
}

public sealed class DiskAndSensorTests
{
    [Theory]
    [InlineData("Hitachi HTS542525K9SA00", "SATA", uint.MaxValue, DiskMediaType.Hdd)]
    [InlineData("ST1000DM010-2EP102", "SATA", uint.MaxValue, DiskMediaType.Hdd)]
    [InlineData("WDC WD10EZEX-08WN4A0", "SATA", uint.MaxValue, DiskMediaType.Hdd)]
    [InlineData("KINGSTON SA400S37240G", "SATA", uint.MaxValue, DiskMediaType.Ssd)]
    [InlineData("Samsung SSD 870 EVO 500GB", "SATA", uint.MaxValue, DiskMediaType.Ssd)]
    [InlineData("Qualquer", "NVMe", uint.MaxValue, DiskMediaType.Ssd)]
    [InlineData("Qualquer", "SATA", 7200u, DiskMediaType.Hdd)]
    [InlineData("Qualquer", "SATA", 0u, DiskMediaType.Ssd)]
    [InlineData("Qualquer", "SATA", uint.MaxValue, DiskMediaType.Unknown)]
    public void Infers_disk_type_when_windows_does_not_report(string model, string bus, uint spindle, DiskMediaType expected) =>
        Assert.Equal(expected, DiskTypeHeuristics.Infer(model, DiskMediaType.Unknown, bus, spindle));

    [Fact]
    public void Reported_type_wins() =>
        Assert.Equal(DiskMediaType.Ssd, DiskTypeHeuristics.Infer("HTS5425", DiskMediaType.Ssd, "SATA", 5400));

    [Fact]
    public void Zero_readings_from_sensors_without_access_become_unavailable()
    {
        Assert.Null(SensorValues.Temperature(0));
        Assert.Equal(55f, SensorValues.Temperature(55));
        Assert.Null(SensorValues.Positive(0));
        Assert.Equal(3800f, SensorValues.Positive(3800));
        Assert.Null(SensorValues.Percent(null));
    }
}
