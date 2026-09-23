namespace CODConnect.Core;

public readonly struct MacAddress : IEquatable<MacAddress>
{
    private readonly ulong _value;

    public static readonly MacAddress None = default;
    public static readonly MacAddress Broadcast = new(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF);

    public MacAddress(byte b0, byte b1, byte b2, byte b3, byte b4, byte b5)
    {
        _value = ((ulong)b0 << 40) | ((ulong)b1 << 32) | ((ulong)b2 << 24) | ((ulong)b3 << 16) | ((ulong)b4 << 8) | b5;
    }

    public MacAddress(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 6)
        {
            throw new ArgumentException("A MAC address is 6 bytes.", nameof(bytes));
        }

        _value = ((ulong)bytes[0] << 40) | ((ulong)bytes[1] << 32) | ((ulong)bytes[2] << 24)
               | ((ulong)bytes[3] << 16) | ((ulong)bytes[4] << 8) | bytes[5];
    }

    public bool IsNone => _value == 0;
    public bool IsBroadcast => _value == Broadcast._value;
    public bool IsMulticast => (_value & 0x010000000000UL) != 0;

    public void WriteTo(Span<byte> destination)
    {
        destination[0] = (byte)(_value >> 40);
        destination[1] = (byte)(_value >> 32);
        destination[2] = (byte)(_value >> 24);
        destination[3] = (byte)(_value >> 16);
        destination[4] = (byte)(_value >> 8);
        destination[5] = (byte)_value;
    }

    public byte[] ToArray()
    {
        var bytes = new byte[6];
        WriteTo(bytes);
        return bytes;
    }

    public static bool TryParse(string? text, out MacAddress address)
    {
        address = None;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var compact = text.Trim().Replace(":", "").Replace("-", "").Replace(".", "");
        if (compact.Length != 12 || !compact.All(Uri.IsHexDigit))
        {
            return false;
        }

        address = new MacAddress(Convert.FromHexString(compact));
        return true;
    }

    public override string ToString()
        => $"{(byte)(_value >> 40):X2}:{(byte)(_value >> 32):X2}:{(byte)(_value >> 24):X2}:{(byte)(_value >> 16):X2}:{(byte)(_value >> 8):X2}:{(byte)_value:X2}";

    public bool Equals(MacAddress other) => _value == other._value;
    public override bool Equals(object? obj) => obj is MacAddress other && Equals(other);
    public override int GetHashCode() => _value.GetHashCode();
    public static bool operator ==(MacAddress left, MacAddress right) => left.Equals(right);
    public static bool operator !=(MacAddress left, MacAddress right) => !left.Equals(right);
}
