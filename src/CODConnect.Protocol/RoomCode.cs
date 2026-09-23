using System.Security.Cryptography;

namespace CODConnect.Protocol;

public static class RoomCode
{
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";
    public const int PrefixLength = 4;
    public const int SuffixLength = 2;

    public static string Generate()
    {
        Span<char> chars = stackalloc char[PrefixLength + 1 + SuffixLength];
        Span<byte> bytes = stackalloc byte[PrefixLength + SuffixLength];
        RandomNumberGenerator.Fill(bytes);
        for (var i = 0; i < PrefixLength; i++)
        {
            chars[i] = Alphabet[bytes[i] % Alphabet.Length];
        }

        chars[PrefixLength] = '-';
        for (var i = 0; i < SuffixLength; i++)
        {
            chars[PrefixLength + 1 + i] = Alphabet[bytes[PrefixLength + i] % Alphabet.Length];
        }

        return new string(chars);
    }

    public static bool TryValidate(string? code, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var candidate = code.Trim().ToUpperInvariant();
        if (candidate.Length == PrefixLength + SuffixLength && !candidate.Contains('-'))
        {
            candidate = $"{candidate[..PrefixLength]}-{candidate[PrefixLength..]}";
        }

        if (candidate.Length != PrefixLength + 1 + SuffixLength || candidate[PrefixLength] != '-')
        {
            return false;
        }

        for (var i = 0; i < candidate.Length; i++)
        {
            if (i == PrefixLength)
            {
                continue;
            }

            if (!Alphabet.Contains(candidate[i]))
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }
}
