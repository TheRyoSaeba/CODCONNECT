using CODConnect.Protocol;

namespace CODConnect.UI;

public static class RoomBadge
{
    public static string Describe(Screen screen, IReadOnlyList<IpcPlayer> players, string? recentEvent = null)
    {
        if (screen == Screen.Home)
        {
            return "Not connected";
        }

        if (recentEvent is not null)
        {
            return recentEvent;
        }

        var joined = players.Where(p => p.Connected).Select(p => Short(p.Name)).ToList();
        return joined.Count switch
        {
            1 => $"{joined[0]} joined",
            2 => $"{joined[0]} and {joined[1]} joined",
            > 2 => $"{joined[0]} and {joined.Count - 1} others joined",
            _ when players.Count == 1 => $"Connecting to {Short(players[0].Name)}",
            _ when players.Count > 1 => $"Connecting to {players.Count} friends",
            _ => screen switch
            {
                Screen.Hosting => "Waiting for friends",
                Screen.Connecting => "Connecting to friend",
                _ => "Friend joined",
            },
        };
    }

    public static string PlayerDetail(IpcPlayer player)
    {
        var state = !player.Connected ? "Connecting" : player.Console ?? "No console yet";
        return player.Relay && player.Connected ? state + ", via relay" : state;
    }

    public static string FriendsSummary(IReadOnlyList<IpcPlayer> players)
        => $"{players.Count(p => p.ConsoleReady)} of {players.Count} consoles ready";

    public static string Short(string name) => name.Length <= 14 ? name : name[..13] + "…";

    public static string? Change(IReadOnlyCollection<string> before, IReadOnlyCollection<string> after)
    {
        var arrived = after.Except(before).ToList();
        if (arrived.Count > 0)
        {
            return $"{Short(arrived[0])} joined";
        }

        var left = before.Except(after).ToList();
        return left.Count > 0 ? $"{Short(left[0])} left" : null;
    }
}
