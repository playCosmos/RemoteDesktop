using System.Net;
using System.Net.Sockets;

namespace RemoteDesktop;

public static class HostAddresses
{
    public static IReadOnlyList<string> Endpoints(int port, IEnumerable<IPAddress>? addresses = null)
    {
        if (port is < 1 or > 65535) port = 45831;
        addresses ??= Dns.GetHostAddresses(Dns.GetHostName());
        var ipv4 = addresses.Where(ip => ip.AddressFamily == AddressFamily.InterNetwork).Select(ip => ip.ToString()).Distinct(StringComparer.Ordinal).ToList();
        var shareable = ipv4.Where(ip => IPAddress.Parse(ip).GetAddressBytes()[0] != 127).ToList();
        if (shareable.Count == 0) shareable = ipv4;
        shareable.Sort(Compare);
        return shareable.Select(ip => ip + ":" + port).ToList();
    }

    private static int Compare(string left, string right)
    {
        var rank = Rank(left).CompareTo(Rank(right));
        if (rank != 0) return rank;
        var a = IPAddress.Parse(left).GetAddressBytes();
        var b = IPAddress.Parse(right).GetAddressBytes();
        for (var i = 0; i < a.Length && i < b.Length; i++)
        {
            var order = a[i].CompareTo(b[i]);
            if (order != 0) return order;
        }
        return a.Length.CompareTo(b.Length);
    }

    private static int Rank(string ip)
    {
        var bytes = IPAddress.Parse(ip).GetAddressBytes();
        if (bytes.Length != 4) return 3;
        if (bytes[0] == 10 || (bytes[0] == 192 && bytes[1] == 168) || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)) return 0;
        if (bytes[0] == 169 && bytes[1] == 254) return 2;
        if (bytes[0] == 127) return 3;
        return 1;
    }
}
