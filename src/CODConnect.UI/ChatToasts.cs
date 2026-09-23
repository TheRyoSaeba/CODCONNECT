using CommunityToolkit.WinUI.Notifications;

namespace CODConnect.UI;

internal static class ChatToasts
{
    private const string Group = "room-chat";
    private static Action? _activated;
    private static bool _listening;

    public static Action? Activated
    {
        get => _activated;
        set
        {
            _activated = value;
            if (_listening) return;
            _listening = true;
            ToastNotificationManagerCompat.OnActivated += _ => _activated?.Invoke();
        }
    }

    public static void Show(string author, string text)
    {
        try
        {
            new ToastContentBuilder()
                .AddArgument("open", "chat")
                .AddText(author)
                .AddText(text.Length > 200 ? text[..200] + "…" : text)
                .Show(toast => { toast.Group = Group; toast.ExpirationTime = DateTimeOffset.Now.AddHours(1); });
        }
        catch (Exception)
        {
        }
    }

    public static void Clear()
    {
        try { ToastNotificationManagerCompat.History.RemoveGroup(Group); }
        catch (Exception) { }
    }
}
