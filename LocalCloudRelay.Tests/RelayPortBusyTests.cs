using System.Net;
using System.Net.Sockets;
using LocalCloudRelay;

namespace LocalCloudRelay.Tests;

public sealed class RelayPortBusyTests
{
    [Fact]
    public async Task StartOnBusyPortThrowsAndReportsNotRunning()
    {
        // Hold the port the way another process would, so Kestrel's bind must fail.
        var blocker = new TcpListener(IPAddress.Any, 0);
        blocker.Start();
        try
        {
            var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
            using var server = new RelayServer(new RelayTelemetryStore(), port: port);

            await Assert.ThrowsAnyAsync<Exception>(server.StartAsync);

            Assert.False(server.IsRunning);
        }
        finally
        {
            blocker.Stop();
        }
    }
}
