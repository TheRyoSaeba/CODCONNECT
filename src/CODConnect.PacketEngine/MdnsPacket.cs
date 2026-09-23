using System.Text;

namespace CODConnect.PacketEngine;

public static class MdnsPacket
{
    public const ushort Port = 5353;

    public static bool TryGetOwnHostName(ReadOnlySpan<byte> payload, IPv4Address sender, out string name)
    {
        name = string.Empty;
        if (payload.Length < 12 || (payload[2] & 0x80) == 0)
        {
            return false;
        }

        var questions = (payload[4] << 8) | payload[5];
        var records = ((payload[6] << 8) | payload[7]) + ((payload[8] << 8) | payload[9]) + ((payload[10] << 8) | payload[11]);
        var offset = 12;
        for (var i = 0; i < questions; i++)
        {
            if (!TryReadName(payload, ref offset, out _) || offset + 4 > payload.Length)
            {
                return false;
            }

            offset += 4;
        }

        for (var i = 0; i < records; i++)
        {
            if (!TryReadName(payload, ref offset, out var owner) || offset + 10 > payload.Length)
            {
                return false;
            }

            var type = (payload[offset] << 8) | payload[offset + 1];
            var length = (payload[offset + 8] << 8) | payload[offset + 9];
            offset += 10;
            if (offset + length > payload.Length)
            {
                return false;
            }

            if (type == 1 && length == 4 && new IPv4Address(payload.Slice(offset, 4)) == sender)
            {
                name = owner.EndsWith(".local", StringComparison.OrdinalIgnoreCase) ? owner[..^6] : owner;
                return name.Length > 0;
            }

            offset += length;
        }

        return false;
    }

    private static bool TryReadName(ReadOnlySpan<byte> payload, ref int offset, out string name)
    {
        var builder = new StringBuilder();
        var position = offset;
        var jumped = false;
        for (var hops = 0; hops < 32; hops++)
        {
            if (position >= payload.Length)
            {
                name = string.Empty;
                return false;
            }

            var length = payload[position];
            if (length == 0)
            {
                if (!jumped)
                {
                    offset = position + 1;
                }

                name = builder.ToString();
                return true;
            }

            if ((length & 0xC0) == 0xC0)
            {
                if (position + 1 >= payload.Length)
                {
                    name = string.Empty;
                    return false;
                }

                if (!jumped)
                {
                    offset = position + 2;
                }

                jumped = true;
                position = ((length & 0x3F) << 8) | payload[position + 1];
                continue;
            }

            if (position + 1 + length > payload.Length)
            {
                name = string.Empty;
                return false;
            }

            if (builder.Length > 0)
            {
                builder.Append('.');
            }

            builder.Append(Encoding.UTF8.GetString(payload.Slice(position + 1, length)));
            position += 1 + length;
        }

        name = string.Empty;
        return false;
    }
}
