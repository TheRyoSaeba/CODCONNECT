namespace CODConnect.Discovery;

public static class ConsoleDetector
{
    public static string? DetectConsoleType(MacAddress mac, string? dhcpHostname = null, string? ssdpServerHeader = null, string? vendorClass = null)
    {
        var fromText = DetectFromText($"{vendorClass} {dhcpHostname} {ssdpServerHeader}");
        if (fromText is not null)
        {
            return fromText;
        }

        var compact = mac.ToString().Replace(":", "");
        if (compact.Length >= 6 && PlayStationOui.Prefixes.Contains(compact[..6]))
        {
            return "PlayStation";
        }

        return null;
    }

    private static string? DetectFromText(string text)
    {
        var haystack = text.ToUpperInvariant();
        foreach (var model in new[] { "PS5", "PS4", "PS3" })
        {
            if (haystack.Contains(model))
            {
                return model;
            }
        }

        if (haystack.Contains("PLAYSTATION"))
        {
            return "PlayStation";
        }

        if (haystack.Contains("XBOX"))
        {
            return "Xbox";
        }

        return null;
    }
}
