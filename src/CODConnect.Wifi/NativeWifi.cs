using System.Runtime.InteropServices;

namespace CODConnect.Wifi;

public sealed record WlanInterface(Guid Id, string Description, bool Connected, int? Channel, bool? HostedNetworkCapable)
{
    public string? Band => Channel switch
    {
        null => null,
        <= 14 => "2.4 GHz",
        _ => "5 GHz",
    };

    public bool OnDfsChannel => Channel is >= 52 and <= 144;
}

public static class NativeWifi
{
    private const uint ClientVersion = 2;
    private const int OpcodeInterfaceState = 6;
    private const int OpcodeChannelNumber = 8;
    private const int OpcodeHostedNetworkCapable = 15;
    private const int InterfaceStateConnected = 1;
    private const int InterfaceInfoSize = 16 + 512 + 4;

    public static IReadOnlyList<WlanInterface> Interfaces()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        IntPtr handle;
        try
        {
            if (WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle) != 0)
            {
                return [];
            }
        }
        catch (DllNotFoundException)
        {
            return [];
        }

        try
        {
            if (WlanEnumInterfaces(handle, IntPtr.Zero, out var list) != 0)
            {
                return [];
            }

            try
            {
                var count = Marshal.ReadInt32(list);
                var result = new List<WlanInterface>(count);
                for (var i = 0; i < count; i++)
                {
                    var item = list + 8 + (i * InterfaceInfoSize);
                    var guidBytes = new byte[16];
                    Marshal.Copy(item, guidBytes, 0, 16);
                    var id = new Guid(guidBytes);
                    var description = Marshal.PtrToStringUni(item + 16, 256).TrimEnd('\0');

                    var state = QueryInt(handle, id, OpcodeInterfaceState);
                    var connected = state == InterfaceStateConnected;
                    var channel = connected ? QueryInt(handle, id, OpcodeChannelNumber) : null;
                    var hosted = QueryInt(handle, id, OpcodeHostedNetworkCapable);
                    result.Add(new WlanInterface(id, description, connected, channel, hosted is null ? null : hosted != 0));
                }

                return result;
            }
            finally
            {
                WlanFreeMemory(list);
            }
        }
        finally
        {
            WlanCloseHandle(handle, IntPtr.Zero);
        }
    }

    private static int? QueryInt(IntPtr handle, Guid id, int opcode)
    {
        if (WlanQueryInterface(handle, ref id, opcode, IntPtr.Zero, out var size, out var data, out _) != 0 || data == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return size >= 4 ? Marshal.ReadInt32(data) : null;
        }
        finally
        {
            WlanFreeMemory(data);
        }
    }

    [DllImport("wlanapi.dll")]
    private static extern uint WlanOpenHandle(uint clientVersion, IntPtr reserved, out uint negotiatedVersion, out IntPtr clientHandle);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanCloseHandle(IntPtr clientHandle, IntPtr reserved);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanEnumInterfaces(IntPtr clientHandle, IntPtr reserved, out IntPtr interfaceList);

    [DllImport("wlanapi.dll")]
    private static extern uint WlanQueryInterface(IntPtr clientHandle, ref Guid interfaceGuid, int opcode, IntPtr reserved, out uint dataSize, out IntPtr data, out int valueType);

    [DllImport("wlanapi.dll")]
    private static extern void WlanFreeMemory(IntPtr memory);
}
