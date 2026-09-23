using System.Runtime.InteropServices;
using System.Text;

namespace CODConnect.PacketEngine.Native;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct pcap_pkthdr
{
    public uint TsSec;
    public uint TsUsec;
    public uint CapLen;
    public uint Len;
}

[StructLayout(LayoutKind.Sequential)]
internal struct pcap_if
{
    public IntPtr Next;
    [MarshalAs(UnmanagedType.LPStr)] public string Name;
    [MarshalAs(UnmanagedType.LPStr)] public string Description;
    public IntPtr Addresses;
    public uint Flags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct pcap_addr
{
    public IntPtr Next;
    public IntPtr Addr;
    public IntPtr Netmask;
    public IntPtr Broadaddr;
    public IntPtr Dstaddr;
}

[StructLayout(LayoutKind.Sequential)]
internal struct bpf_program
{
    public uint BfLen;
    public IntPtr BfInsns;
}

internal static class PcapNative
{
    private const string Lib = "wpcap.dll";
    internal const uint PcapNetmaskUnknown = 0xFFFFFFFF;

    static PcapNative()
    {
        if (NativeLibrary.TryLoad(Lib, out _))
        {
            return;
        }

        var npcapDir = Path.Combine(Environment.SystemDirectory, "Npcap");
        if (Directory.Exists(npcapDir) && NativeLibrary.TryLoad(Path.Combine(npcapDir, Lib), out _))
        {
            return;
        }

        throw new DllNotFoundException(
            "Npcap (wpcap.dll) was not found. Install Npcap from https://npcap.com and re-run.");
    }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_findalldevs(out IntPtr alldevs, StringBuilder errbuf);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_freealldevs(IntPtr alldevs);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr pcap_open_live(string device, int snaplen, int promisc, int toMs, StringBuilder errbuf);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_close(IntPtr handle);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_next_ex(IntPtr handle, out IntPtr header, out IntPtr data);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_sendpacket(IntPtr handle, byte[] buffer, int size);

    internal const int PcapDirectionIn = 1;

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_setdirection(IntPtr handle, int direction);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_compile(IntPtr handle, out bpf_program program, string filter, int optimize, uint netmask);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int pcap_setfilter(IntPtr handle, ref bpf_program program);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void pcap_freecode(ref bpf_program program);

    [DllImport(Lib, EntryPoint = "pcap_geterr", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pcap_geterr_native(IntPtr handle);

    internal static string pcap_geterr(IntPtr handle)
        => Marshal.PtrToStringAnsi(pcap_geterr_native(handle)) ?? "unknown pcap error";
}
