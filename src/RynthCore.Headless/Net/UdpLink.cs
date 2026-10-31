// ============================================================================
//  RynthCore.Headless - Net/UdpLink.cs
//  One UDP socket per session. Everything goes to the server's port except the
//  ConnectResponse, which goes to port + 1; the server answers from both, so we
//  accept datagrams from either (and nothing from any other address).
//  Non-blocking: the session's tick drains it; Wait() sleeps until data or a timeout.
// ============================================================================

using System.Net;
using System.Net.Sockets;

namespace RynthCore.Headless.Net;

public sealed class UdpLink : IDatagramLink, IDisposable
{
    private const int SioUdpConnReset = unchecked((int)0x9800000C);

    private readonly Socket _socket;
    private readonly IPEndPoint _server, _connect;
    private readonly byte[] _buf = new byte[2048];
    private EndPoint _from = new IPEndPoint(IPAddress.Any, 0);

    public UdpLink(IPAddress host, int port)
    {
        _server = new IPEndPoint(host, port);
        _connect = new IPEndPoint(host, port + 1);
        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { Blocking = false };
        if (OperatingSystem.IsWindows())
            _socket.IOControl(SioUdpConnReset, new byte[4], null);   // an ICMP "port unreachable" must not break receives
        _socket.Bind(new IPEndPoint(IPAddress.Any, 0));
    }

    public static UdpLink Open(string host, int port)
    {
        IPAddress ip = IPAddress.TryParse(host, out IPAddress? a)
            ? a
            : Dns.GetHostAddresses(host).First(x => x.AddressFamily == AddressFamily.InterNetwork);
        return new UdpLink(ip, port);
    }

    public long Ignored { get; private set; }

    /// <summary>Sees every datagram (outbound = true for ours). For captures; must not keep the span.</summary>
    public Action<bool, ReadOnlySpan<byte>>? Tap { get; set; }

    void IDatagramLink.Send(ReadOnlySpan<byte> datagram, bool connectPort)
    {
        Tap?.Invoke(true, datagram);
        _socket.SendTo(datagram, SocketFlags.None, connectPort ? _connect : _server);
    }

    bool IDatagramLink.TryReceive(out byte[] datagram)
    {
        datagram = Array.Empty<byte>();
        while (_socket.Available > 0)
        {
            int n;
            try
            {
                n = _socket.ReceiveFrom(_buf, ref _from);
            }
            catch (SocketException e) when (e.SocketErrorCode is SocketError.WouldBlock or SocketError.ConnectionReset or SocketError.MessageSize)
            {
                continue;
            }
            if (_from is not IPEndPoint ep || !ep.Address.Equals(_server.Address)
                || (ep.Port != _server.Port && ep.Port != _connect.Port))
            {
                Ignored++;
                continue;
            }
            datagram = _buf.AsSpan(0, n).ToArray();
            Tap?.Invoke(false, datagram);
            return true;
        }
        return false;
    }

    /// <summary>Sleeps until a datagram is waiting or the timeout passes.</summary>
    public void Wait(int timeoutMs) => _socket.Poll(timeoutMs * 1000, SelectMode.SelectRead);

    /// <summary>Sleeps until any of the links has a datagram waiting, or the timeout passes (several sessions, one thread).</summary>
    public static void WaitAny(IReadOnlyList<UdpLink> links, int timeoutMs)
    {
        if (links.Count == 0) { Thread.Sleep(timeoutMs); return; }
        if (links.Count == 1) { links[0].Wait(timeoutMs); return; }
        var read = new List<Socket>(links.Count);
        foreach (UdpLink l in links) read.Add(l._socket);
        try { Socket.Select(read, null, null, timeoutMs * 1000); }
        catch (ObjectDisposedException) { Thread.Sleep(1); }
    }

    public void Dispose() => _socket.Dispose();
}
