using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace SignRelay.Server.Util;

/// <summary>
/// Discovers local host IP networks (similar to Nefarius.Utilities.AspNetCore) for forwarded-headers trust.
/// </summary>
internal static class NetworkUtil
{
    /// <summary>
    /// Enumerates CIDR networks for each up interface's unicast addresses (IPv4/IPv6).
    /// </summary>
    public static IEnumerable<IPNetwork> GetLocalNetworks()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up)
                continue;

            foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
            {
                if (unicast.Address.AddressFamily is not (AddressFamily.InterNetwork or AddressFamily.InterNetworkV6))
                    continue;

                IPNetwork net;
                try
                {
                    net = ToNetwork(unicast);
                }
                catch (FormatException)
                {
                    continue;
                }
                catch (ArgumentException)
                {
                    continue;
                }

                yield return net;
            }
        }
    }

    private static IPNetwork ToNetwork(UnicastIPAddressInformation info)
    {
        var prefix = GetNetworkPrefix(info);
        return IPNetwork.Parse($"{prefix}/{info.PrefixLength}");
    }

    /// <summary>
    /// Computes the network prefix address from the IP and prefix length (CIDR semantics).
    /// </summary>
    private static IPAddress GetNetworkPrefix(UnicastIPAddressInformation info)
    {
        if (info.PrefixLength == 0)
            return info.Address.AddressFamily == AddressFamily.InterNetwork ? IPAddress.Any : IPAddress.IPv6Any;

        var maxPrefix = info.Address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        if (info.PrefixLength == maxPrefix)
            return info.Address;

        if (info.PrefixLength > maxPrefix)
            throw new ArgumentOutOfRangeException(nameof(info), info.PrefixLength, "Invalid prefix length.");

        var bytes = info.Address.GetAddressBytes();
        var bitsToBeZeroed = maxPrefix - info.PrefixLength;

        var i = bytes.Length;
        while (i-- > 0 && bitsToBeZeroed >= 8)
        {
            bytes[i] = 0;
            bitsToBeZeroed -= 8;
        }

        if (bitsToBeZeroed > 0)
            bytes[i] &= (byte)(byte.MaxValue << bitsToBeZeroed);

        return info.Address.AddressFamily == AddressFamily.InterNetwork
            ? new IPAddress(bytes)
            : new IPAddress(bytes, info.Address.ScopeId);
    }
}
