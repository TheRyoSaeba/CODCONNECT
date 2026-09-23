namespace CODConnect.Core;

public readonly struct IPv4Address : IEquatable<IPv4Address>
{
    private readonly uint _value;

    public static readonly IPv4Address None = default;
    public static readonly IPv4Address Broadcast = new(255, 255, 255, 255);

    private IPv4Address(uint rawValue) => _value = rawValue;

    public IPv4Address(byte a, byte b, byte c, byte d)
        => _value = ((uint)a << 24) | ((uint)b << 16) | ((uint)c << 8) | d;

    public IPv4Address(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 4)
        {
            throw new ArgumentException("An IPv4 address is 4 bytes.", nameof(bytes));
        }

        _value = ((uint)bytes[0] << 24) | ((uint)bytes[1] << 16) | ((uint)bytes[2] << 8) | bytes[3];
    }

    public bool IsNone => _value == 0;
    public bool IsBroadcast => _value == Broadcast._value;

    public uint ToUInt32() => _value;

    public static IPv4Address FromUInt32(uint value) => new(value);

    public void WriteTo(Span<byte> destination)
    {
        destination[0] = (byte)(_value >> 24);
        destination[1] = (byte)(_value >> 16);
        destination[2] = (byte)(_value >> 8);
        destination[3] = (byte)_value;
    }

    public byte[] ToArray()
    {
        var bytes = new byte[4];
        WriteTo(bytes);
        return bytes;
    }

    public static bool TryParse(string? text, out IPv4Address address)
    {
        address = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('.');
        if (parts.Length != 4)
        {
            return false;
        }

        var octets = new byte[4];
        for (var i = 0; i < 4; i++)
        {
            if (!byte.TryParse(parts[i], out var octet))
            {
                return false;
            }

            octets[i] = octet;
        }

        address = new IPv4Address(octets);
        return true;
    }

    public override string ToString()
        => $"{(byte)(_value >> 24)}.{(byte)(_value >> 16)}.{(byte)(_value >> 8)}.{(byte)_value}";

    public bool Equals(IPv4Address other) => _value == other._value;
    public override bool Equals(object? obj) => obj is IPv4Address other && Equals(other);
    public override int GetHashCode() => (int)_value;
    public static bool operator ==(IPv4Address left, IPv4Address right) => left.Equals(right);
    public static bool operator !=(IPv4Address left, IPv4Address right) => !left.Equals(right);
}
