using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using CODConnect.Protocol;

namespace CODConnect.UI;

public partial class MainWindow
{
    private TextBlock? _devFeedback;
    private string? _devMessage;
    private bool _editingServer;

    private async void ShowDeveloper()
    {
        DetailContent.Children.Add(Text("Most of the time you probably don’t need these.", true));
        Space((FrameworkElement)DetailContent.Children[^1], 0, 20);
        var loading = Text("Loading…", true, 12); DetailContent.Children.Add(loading);

        var settings = await _vm.LoadDevSettingsAsync();
        if (_page != "Developer") return;
        DetailContent.Children.Remove(loading);
        if (settings is null)
        {
            var unavailable = Text(_vm.DevError ?? "Developer options need the CODCONNECT service.", true, 12);
            DetailContent.Children.Add(unavailable);
            return;
        }

        DevToggle("Let friends FTP to my console",
            "Gives people in your room full access to your console’s files, so turn it on only with friends you trust.",
            settings.FriendsConsoleAccess, on => settings with { FriendsConsoleAccess = on });
        DevToggle("Always join through the relay",
            "Slower than a direct connection, but useful when a direct one keeps dropping.",
            settings.RelayOnly, on => settings with { RelayOnly = on });
        DevRoomServer(settings);

        _devFeedback = Text(_devMessage ?? "", true, 11); _devMessage = null; _devFeedback.Foreground = Color("WarnBrush"); _devFeedback.MinHeight = 18;
        AutomationProperties.SetLiveSetting(_devFeedback, AutomationLiveSetting.Polite);
        DetailContent.Children.Add(_devFeedback);
    }

    private void DevToggle(string title, string explanation, bool value, Func<bool, IpcDevSettings> change)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 20) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var words = new StackPanel { Margin = new Thickness(0, 0, 20, 0) };
        words.Children.Add(Text(title, size: 14));
        var detail = Text(explanation, true, 12); Space(detail, 6); words.Children.Add(detail);
        row.Children.Add(words);

        var toggle = new CheckBox { IsChecked = value, Style = (Style)FindResource("Switch"), VerticalAlignment = VerticalAlignment.Top };
        AutomationProperties.SetName(toggle, title);
        toggle.Click += async (_, _) =>
        {
            toggle.IsEnabled = false;
            var error = await _vm.SaveDevSettingsAsync(change(toggle.IsChecked == true));
            toggle.IsEnabled = true;
            _devMessage = error;
            Navigate("Developer");
        };
        Grid.SetColumn(toggle, 1); row.Children.Add(toggle);
        DetailContent.Children.Add(row);
    }

    private void DevRoomServer(IpcDevSettings settings)
    {
        DetailContent.Children.Add(Text("Room server", size: 14));
        var custom = !string.Equals(settings.RoomServer, settings.DefaultRoomServer, StringComparison.OrdinalIgnoreCase);
        var detail = Text(custom
            ? "You are using your own room server. Friends must use the same one to find your rooms."
            : "Where rooms are found. Change it only if CODCONNECT’s server is down and you run your own.", true, 12);
        Space(detail, 6, 10); DetailContent.Children.Add(detail);

        var address = new TextBox { Text = settings.RoomServer, IsEnabled = _editingServer, MaxLength = 200 };
        AutomationProperties.SetName(address, "Room server address");
        DetailContent.Children.Add(address);

        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 10) };
        if (!_editingServer)
        {
            var change = Button("Change…", () =>
            {
                var answer = MessageBox.Show(this,
                    "Rooms are found through this server. Whoever runs it sees room codes, player names and the addresses friends use to reach you."
                    + Environment.NewLine + Environment.NewLine
                    + "Only use a server you run or trust, and give your friends the same address - players on different servers cannot see each other's rooms."
                    + Environment.NewLine + Environment.NewLine
                    + "Change the room server?",
                    "Change the room server", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
                if (answer != MessageBoxResult.Yes) return;
                _editingServer = true;
                Navigate("Developer");
            });
            change.IsEnabled = _vm.IsHome;
            change.ToolTip = _vm.IsHome ? null : "Leave the room first.";
            change.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(change);
            if (custom)
            {
                buttons.Children.Add(Button("Use CODCONNECT’s server", async () => await SaveServerAsync(settings with { RoomServer = settings.DefaultRoomServer })));
                ((Button)buttons.Children[^1]).IsEnabled = _vm.IsHome;
            }
        }
        else
        {
            var save = Button("Save", async () => await SaveServerAsync(settings with { RoomServer = address.Text }), true);
            save.Margin = new Thickness(0, 0, 8, 0); buttons.Children.Add(save);
            buttons.Children.Add(Button("Cancel", () => { _editingServer = false; Navigate("Developer"); }));
        }

        DetailContent.Children.Add(buttons);
    }

    private async Task SaveServerAsync(IpcDevSettings wanted)
    {
        if (_devFeedback is not null) _devFeedback.Text = "Checking the server…";
        var error = await _vm.SaveDevSettingsAsync(wanted);
        if (error is null) _editingServer = false;
        _devMessage = error;
        Navigate("Developer");
    }
}
