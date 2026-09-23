using CODConnect.PacketEngine;

namespace CODConnect.PocTool;

public static class AdapterSelector
{
    public static PcapAdapterInfo? Resolve(string? selector, out string error)
    {
        error = string.Empty;
        if (!PcapAdapters.IsAvailable)
        {
            error = "Npcap is not installed. Download the installer from https://npcap.com and re-run.";
            return null;
        }

        var adapters = PcapAdapters.Enumerate();
        if (adapters.Count == 0)
        {
            error = "No Npcap adapters found.";
            return null;
        }

        if (selector is null)
        {
            error = "An adapter name or index is required. Run 'list-adapters' first.";
            return null;
        }

        if (int.TryParse(selector, out var index))
        {
            if (index < 0 || index >= adapters.Count)
            {
                error = $"Adapter index {index} is out of range (0..{adapters.Count - 1}).";
                return null;
            }

            return adapters[index];
        }

        var byName = adapters.FirstOrDefault(a =>
            a.PcapName.Equals(selector, StringComparison.OrdinalIgnoreCase)
            || a.FriendlyName.Contains(selector, StringComparison.OrdinalIgnoreCase));
        if (byName is null)
        {
            error = $"No adapter matches '{selector}'. Run 'list-adapters' first.";
            return null;
        }

        return byName;
    }
}
