using Soulcrest.App.Network;
using Soulcrest.Core.Network;
using Xunit;
using Xunit.Abstractions;

namespace Soulcrest.App.Tests;

/// <summary>
/// Diagnose: reads a local game capture (SOULCREST_PCAP, server ports in SOULCREST_PCAP_PORTS, e.g. the
/// relay port "57117") with the app's network loot path and prints every soul message. Captures stay
/// local (they contain character names), so there is no fixture in the repository.
/// </summary>
public sealed class NetworkCaptureExploration(ITestOutputHelper output)
{
    [Fact]
    public void ReadsTheLootMessagesOfACapture()
    {
        var path = Environment.GetEnvironmentVariable("SOULCREST_PCAP");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
            return;
        var ports = (Environment.GetEnvironmentVariable("SOULCREST_PCAP_PORTS") ?? "57117").Split(',').Select(int.Parse).ToHashSet();
        var souls = NetworkLootService.ReadCaptureFile(path, ports);
        foreach (var (at, gain) in souls)
            output.WriteLine($"{at.ToLocalTime():HH:mm:ss.fff} #{gain.PetNumber} {PetIdTable.Find(gain.PetNumber)?.En ?? "?"} x{gain.Souls}");
        output.WriteLine($"{souls.Count} Einträge, {souls.Sum(s => s.Gain.Souls)} Souls");
    }
}
