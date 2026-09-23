using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CODConnect.UI;

public partial class MainWindow
{
    private void ShowHelp()
    {
        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 20) };
        foreach (var (label, troubleshooting) in new[] { ("Setup", false), ("Troubleshooting", true) })
        {
            var tab = Button(label, () => { _troubleshooting = troubleshooting; Navigate("Guide"); });
            tab.Background = _troubleshooting == troubleshooting ? Color("PanelLightBrush") : Brushes.Transparent;
            tab.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetItemStatus(tab, _troubleshooting == troubleshooting ? "Selected" : "Not selected");
            tabs.Children.Add(tab);
        }
        DetailContent.Children.Add(tabs);
        if (_troubleshooting) { ShowTroubleshooting(); return; }

        var wifi = _helpWifi ?? (_vm.IsHome ? _vm.WifiMode : _vm.WifiNetwork is not null);
        DetailContent.Children.Add(Text("Get ready to play", size: 20));
        var modes = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 20) };
        foreach (var (label, isWifi) in new[] { ("Ethernet", false), ("Wi-Fi", true) })
        {
            var mode = Button(label, () => { _helpWifi = isWifi; Navigate("Guide"); });
            mode.Background = wifi == isWifi ? Color("PanelLightBrush") : Brushes.Transparent;
            mode.Margin = new Thickness(0, 0, 8, 0);
            AutomationProperties.SetItemStatus(mode, wifi == isWifi ? "Selected" : "Not selected");
            modes.Children.Add(mode);
        }
        DetailContent.Children.Add(modes);

        if (wifi)
        {
            HelpStep("1", "Start a room in Wi-Fi mode", "Choose Wi-Fi, then create a room or join your friend’s. This PC starts a network for your console.");
            HelpStep("2", "Connect your console", "On the console, join the network shown on the room screen and enter its password.");
        }
        else
        {
            HelpStep("1", "Plug in your console", "Use a spare Ethernet port on this PC. Keep this PC’s Internet on Wi-Fi or another port.");
            HelpStep("2", "Meet in a room", "Create a room and share the code, or join your friend’s.");
        }

        HelpStep("3", "Open the game’s LAN menu", "When the center ring shows LAN ready, you and your friend are on one network.");
        var tip = Text("Set the console’s network to Automatic. It gets Internet through its own PC, so sign-in and updates work in a room.", true, 12);
        tip.TextWrapping = TextWrapping.Wrap; Space(tip, 4, 0); DetailContent.Children.Add(tip);
    }

    private void ShowTroubleshooting()
    {
        var topics = new WrapPanel { Margin = new Thickness(0, 0, 0, 16) };
        foreach (var (label, key) in new[] { ("Console not detected", "console"), ("Friend can’t connect", "friend"), ("Windows Firewall", "firewall") })
        {
            var topic = Button(label, () => { _troubleTopic = key; Navigate("Guide"); });
            topic.Background = _troubleTopic == key ? Color("PanelLightBrush") : Brushes.Transparent;
            topic.Margin = new Thickness(0, 0, 8, 8);
            AutomationProperties.SetItemStatus(topic, _troubleTopic == key ? "Selected" : "Not selected");
            topics.Children.Add(topic);
        }
        DetailContent.Children.Add(topics);

        switch (_troubleTopic)
        {
            case "friend":
                DetailContent.Children.Add(Text("Your friend can’t connect", size: 20));
                Space((FrameworkElement)DetailContent.Children[^1], 0, 16);
                HelpStep("", "Check the room code", "Codes look like K7M4-P2. A room closes when its host leaves - make a new one if needed.");
                HelpStep("", "Keep both PCs in the room", "Leave CODCONNECT open on both PCs. Closing the window is fine; leaving the room is not.");
                HelpStep("", "Still stuck on “Connecting”?", "A strict network can block the link. Check the firewall on both PCs, then try again.");
                break;
            case "firewall":
                ShowFirewallHelp();
                break;
            default:
                DetailContent.Children.Add(Text("Your console isn’t detected", size: 20));
                Space((FrameworkElement)DetailContent.Children[^1], 0, 16);
                HelpStep("", "Ethernet: check the port", "The cable must be in the port chosen on the Console page. Unplug it and plug it back in.");
                HelpStep("", "Wi-Fi: network not in the console’s list", "It shares this PC’s Wi-Fi band. Add it on the console by name (Set Up Manually), or put this PC on 2.4 GHz Wi-Fi or a cable - older consoles only see 2.4 GHz.");
                HelpStep("", "Run the console’s connection test", "With its network set to Automatic, the console gets its address from the room.");
                break;
        }
    }

    private void HelpStep(string number, string title, string body)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 16) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(number.Length == 0 ? 0 : 28) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        var index = Text(number, true, 11); index.Margin = new Thickness(0, 3, 0, 0); row.Children.Add(index);
        var content = new StackPanel(); Grid.SetColumn(content, 1);
        content.Children.Add(Text(title, size: 14));
        var description = Text(body, true, 12); Space(description, 6); content.Children.Add(description);
        row.Children.Add(content); DetailContent.Children.Add(row);
    }

    private void ShowFirewallHelp()
    {
        DetailContent.Children.Add(Text("Windows Firewall", size: 20));
        Space((FrameworkElement)DetailContent.Children[^1], 0, 16);
        HelpStep("1", "Open the allowed-apps list", "Windows Defender Firewall, then Allow an app or feature.");
        HelpStep("2", "Allow CODCONNECT", "Choose Change settings, then Allow another app, and add the app below.");
        var path = FirewallHelp.FindExecutable(_vm.BackendMode);
        var identity = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };
        var executable = Text(path is null ? "App location unavailable" : Path.GetFileName(path), size: 12);
        executable.TextWrapping = TextWrapping.NoWrap; executable.TextTrimming = TextTrimming.CharacterEllipsis; executable.ToolTip = path; identity.Children.Add(executable);
        var role = Text(_vm.BackendMode == "service" ? "Background service" : "Desktop app", true, 11); Space(role, 4); identity.Children.Add(role);
        DetailContent.Children.Add(new Border { Background = Color("PanelLightBrush"), CornerRadius = new CornerRadius(5), Child = identity, Margin = new Thickness(28, 0, 0, 16) });
        var buttons = new WrapPanel { Margin = new Thickness(28, 0, 0, 0) };
        var feedback = Text("", true, 11); feedback.MinHeight = 16; feedback.Margin = new Thickness(28, 0, 0, 0);
        var open = Button("Open firewall settings", () =>
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "control.exe"), "/name Microsoft.WindowsFirewall") { UseShellExecute = true }); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException) { feedback.Text = "Open Windows Defender Firewall from Start."; }
        });
        open.Margin = new Thickness(0, 0, 8, 8); buttons.Children.Add(open);
        var copy = Button("Copy app path", () =>
        {
            try { if (path is not null) { Clipboard.SetText(path); feedback.Text = "Path copied."; } }
            catch { feedback.Text = "Couldn’t copy. Browse to the installation folder."; }
        });
        copy.IsEnabled = path is not null; copy.ToolTip = path ?? "Restart the installed app to locate its network executable.";
        copy.Margin = new Thickness(0, 0, 0, 8); buttons.Children.Add(copy); DetailContent.Children.Add(buttons); DetailContent.Children.Add(feedback);
        var note = Text("Keep the firewall on.", true, 12); Space(note, 4); DetailContent.Children.Add(note);
    }
}
