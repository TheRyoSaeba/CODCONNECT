using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using CODConnect.Core;
using CODConnect.PacketEngine.Native;

namespace CODConnect.PacketEngine;

public sealed record PcapAdapterInfo(
    string PcapName,
    string FriendlyName,
    string Description,
    IReadOnlyList<IPv4Address> Addresses,
    MacAddress Mac,
    LinkState LinkState,
    bool IsLoopback);

public static class PcapAdapters
{
    private static readonly bool IsAvailableField = DetectAvailability();

    public static bool IsAvailable => IsAvailableField;

    private static bool DetectAvailability()
    {
        try
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Native.PcapNative).TypeHandle);
            return true;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (TypeInitializationException)
        {
            return false;
        }
    }

    public static IReadOnlyList<PcapAdapterInfo> Enumerate()
    {
        var nativeInterfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Select(n => (networkInterface: n, properties: n.GetIPProperties()))
            .ToList();

        var result = new List<PcapAdapterInfo>();
        var errbuf = new StringBuilder(256);
        if (PcapNative.pcap_findalldevs(out var head, errbuf) != 0)
        {
            throw new InvalidOperationException($"pcap_findalldevs failed: {errbuf}");
        }

        try
        {
            var current = head;
            while (current != IntPtr.Zero)
            {
                var pcapIf = Marshal.PtrToStructure<pcap_if>(current);
                result.Add(BuildInfo(pcapIf, nativeInterfaces));
                current = pcapIf.Next;
            }
        }
        finally
        {
            PcapNative.pcap_freealldevs(head);
        }

        return result;
    }

    private static PcapAdapterInfo BuildInfo(pcap_if pcapIf, List<(System.Net.NetworkInformation.NetworkInterface networkInterface, IPInterfaceProperties properties)> nativeInterfaces)
    {
        var addresses = new List<IPv4Address>();
        var addressCursor = pcapIf.Addresses;
        while (addressCursor != IntPtr.Zero)
        {
            var addr = Marshal.PtrToStructure<pcap_addr>(addressCursor);
            if (ReadSockaddrInet(addr.Addr) is { } ipv4)
            {
                addresses.Add(ipv4);
            }

            addressCursor = addr.Next;
        }

        var guid = ExtractGuid(pcapIf.Name);
        System.Net.NetworkInformation.NetworkInterface? matched = null;
        if (guid is not null)
        {
            matched = nativeInterfaces.FirstOrDefault(n =>
                    n.networkInterface.Id.Contains(guid, StringComparison.OrdinalIgnoreCase)).networkInterface;
        }

        var isLoopback = pcapIf.Name.Contains("Loopback", StringComparison.OrdinalIgnoreCase)
                         || matched?.NetworkInterfaceType == NetworkInterfaceType.Loopback;
        var linkState = matched?.OperationalStatus == OperationalStatus.Up || isLoopback
            ? LinkState.Up
            : LinkState.Down;
        var mac = ReadMac(matched);
        var friendly = matched?.Name ?? pcapIf.Description ?? pcapIf.Name;

        return new PcapAdapterInfo(pcapIf.Name, friendly, pcapIf.Description ?? string.Empty, addresses, mac, linkState, isLoopback);
    }

    private static MacAddress ReadMac(System.Net.NetworkInformation.NetworkInterface? networkInterface)
    {
        var physical = networkInterface?.GetPhysicalAddress()?.GetAddressBytes();
        return physical is { Length: 6 } bytes ? new MacAddress(bytes) : MacAddress.None;
    }

    private static string? ExtractGuid(string pcapName)
    {
        var open = pcapName.IndexOf('{');
        var close = pcapName.IndexOf('}');
        return open >= 0 && close > open ? pcapName[open..(close + 1)] : null;
    }

    private static IPv4Address? ReadSockaddrInet(IntPtr sockaddr)
    {
        if (sockaddr == IntPtr.Zero)
        {
            return null;
        }

        if (Marshal.ReadInt16(sockaddr) != 2)
        {
            return null;
        }

        var bytes = new byte[4];
        Marshal.Copy(new IntPtr(sockaddr.ToInt64() + 4), bytes, 0, 4);
        return new IPv4Address(bytes);
    }
}
