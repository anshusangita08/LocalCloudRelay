using System.Text;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class StreamTranslationTests
{
    [Fact]
    public async Task RelaySseTranslatorPassesThroughWhenNoTranslatorNeeded()
    {
        // When the source and destination protocols are the same (or no translation needed),
        // ProtocolStreamTranslator.For returns null, and PumpAsync should pass through byte-for-byte.
        var source = new MemoryStream(Encoding.UTF8.GetBytes("data: test\n\n"));
        var destination = new MemoryStream();

        await RelaySseTranslator.PumpAsync(source, destination, WireProtocol.Other, WireProtocol.Other, CancellationToken.None);

        destination.Position = 0;
        var result = Encoding.UTF8.GetString(destination.ToArray());
        Assert.Equal("data: test\n\n", result);
    }

    [Fact]
    public async Task RelaySseTranslatorHandlesMultipleLinesInPassthrough()
    {
        var input = "line1\nline2\nline3\n";
        var source = new MemoryStream(Encoding.UTF8.GetBytes(input));
        var destination = new MemoryStream();

        await RelaySseTranslator.PumpAsync(source, destination, WireProtocol.Other, WireProtocol.Other, CancellationToken.None);

        destination.Position = 0;
        var result = Encoding.UTF8.GetString(destination.ToArray());
        Assert.Equal(input, result);
    }

    [Fact]
    public async Task RelaySseTranslatorHandlesEmptyStream()
    {
        var source = new MemoryStream();
        var destination = new MemoryStream();

        await RelaySseTranslator.PumpAsync(source, destination, WireProtocol.Other, WireProtocol.Other, CancellationToken.None);

        destination.Position = 0;
        Assert.Empty(destination.ToArray());
    }
}

public sealed class FirewallManagerTests
{
    [Fact]
    public void ManualCommandContainsExpectedProfileAndSubnet()
    {
        var cmd = FirewallManager.ManualCommand;

        Assert.Contains("profile=domain,private", cmd);
        Assert.Contains("localsubnet", cmd);
        Assert.Contains("netsh", cmd);
        Assert.Contains($"localport={RelayProtocol.Port}", cmd);
    }

    [Fact]
    public void PowerShellCommandContainsExpectedProfileAndSubnet()
    {
        var cmd = FirewallManager.PowerShellCommand;

        Assert.Contains("Domain,Private", cmd);
        Assert.Contains("LocalSubnet", cmd);
        Assert.Contains("New-NetFirewallRule", cmd);
        Assert.Contains($"LocalPort {RelayProtocol.Port}", cmd);
    }

    [Fact]
    public void RemoveCommandContainsRuleName()
    {
        var cmd = FirewallManager.RemoveCommand;

        Assert.Contains("netsh", cmd);
        Assert.Contains("delete rule", cmd);
    }

    [Fact]
    public void GuidanceTextIncludesTheManualCommand()
    {
        var guidance = FirewallManager.Guidance;

        Assert.Contains("elevated terminal", guidance);
        Assert.Contains(FirewallManager.ManualCommand, guidance);
    }
}
