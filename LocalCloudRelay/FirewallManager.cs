namespace LocalCloudRelay;

/// <summary>
/// Builds the firewall command instead of running it. The previous version shelled out to
/// netsh with Verb="runas" on every relay start, which froze the UI thread behind a UAC
/// prompt on every launch and appended a duplicate rule each time.
/// </summary>
internal static class FirewallManager
{
    private const string RuleName = "Local Cloud Relay (TCP 8787)";

    public static string ManualCommand =>
        $"netsh advfirewall firewall add rule name=\"{RuleName}\" dir=in action=allow protocol=TCP localport={RelayProtocol.Port} profile=domain,private remoteip=localsubnet";

    public static string PowerShellCommand =>
        $"New-NetFirewallRule -DisplayName \"{RuleName}\" -Direction Inbound -Action Allow -Protocol TCP -LocalPort {RelayProtocol.Port} -Profile Domain,Private -RemoteAddress LocalSubnet";

    public static string RemoveCommand =>
        $"netsh advfirewall firewall delete rule name=\"{RuleName}\"";

    public static string Guidance =>
        $"LAN clients need an inbound rule for TCP {RelayProtocol.Port}. If they cannot reach the relay, " +
        $"run this once in an elevated terminal:\n\n{ManualCommand}";
}
