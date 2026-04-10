using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Sockets;
using System.Threading.Tasks;
using RynthCore.App;

namespace RynthCore.App.Avalonia;

internal enum ServerAvailabilityState
{
    Down,
    Up
}

internal sealed class ServerStatusProbeService
{
    private const int ProbeTimeoutMs = 3000;

    // Matches Thwarg's AC login probe payload so the result means
    // "the AC server answered" rather than just "a port is open."
    private static readonly byte[] LoginProbePacket =
    [
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00,
        0x93, 0x00, 0xd0, 0x05, 0x00, 0x00, 0x00, 0x00,
        0x40, 0x00, 0x00, 0x00, 0x04, 0x00, 0x31, 0x38,
        0x30, 0x32, 0x00, 0x00, 0x34, 0x00, 0x00, 0x00,
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x3e, 0xb8, 0xa8, 0x58, 0x1c, 0x00, 0x61, 0x63,
        0x73, 0x65, 0x72, 0x76, 0x65, 0x72, 0x74, 0x72,
        0x61, 0x63, 0x6b, 0x65, 0x72, 0x3a, 0x6a, 0x6a,
        0x39, 0x68, 0x32, 0x36, 0x68, 0x63, 0x73, 0x67,
        0x67, 0x63, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00
    ];

    public async Task<Dictionary<string, ServerAvailabilityState>> ProbeAsync(IEnumerable<LaunchServerProfile> servers)
    {
        ArgumentNullException.ThrowIfNull(servers);

        List<LaunchServerProfile> serverList = servers.ToList();
        var tasks = serverList.Select(async server => new KeyValuePair<string, ServerAvailabilityState>(
            server.Id,
            await ProbeServerAsync(server).ConfigureAwait(false)
                ? ServerAvailabilityState.Up
                : ServerAvailabilityState.Down));

        KeyValuePair<string, ServerAvailabilityState>[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
        return results.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    private static async Task<bool> ProbeServerAsync(LaunchServerProfile server)
    {
        if (server == null ||
            string.IsNullOrWhiteSpace(server.Host) ||
            server.Port <= 0)
        {
            return false;
        }

        using var udpClient = new UdpClient();
        try
        {
            udpClient.Connect(server.Host.Trim(), server.Port);
            await udpClient.SendAsync(LoginProbePacket, LoginProbePacket.Length).ConfigureAwait(false);

            Task<UdpReceiveResult> receiveTask = udpClient.ReceiveAsync();
            Task completedTask = await Task.WhenAny(receiveTask, Task.Delay(ProbeTimeoutMs)).ConfigureAwait(false);
            if (completedTask != receiveTask)
                return false;

            UdpReceiveResult result = await receiveTask.ConfigureAwait(false);
            return result.Buffer is { Length: > 0 };
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
