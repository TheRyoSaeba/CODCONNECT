using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace CODConnect.UI;

public partial class MainWindow : Window
{
    private readonly AppViewModel _vm;
    private Screen _shownScreen = (Screen)(-1);
    private string _page = "Room";
    private bool _initialized;
    private bool _joining;
    private string _joinCode = "";
    private string? _startupError;
    private int _adapterPage;
    private bool _showAllAdapters;
    private bool? _helpWifi;
    private string _troubleTopic = "console";
    private bool _compact;
    private bool _troubleshooting;
    private TextBlock? _status, _counters, _roomCode, _error, _engine, _localConsoleRow, _remoteConsoleRow, _connectionRow, _internetRow;
    private StackPanel? _playerRows;

    internal bool? NpcapMissing { get; set; }

    internal bool? SoftEtherClientMissing { get; set; }
    private string _playerRowsShown = "";
    private readonly List<Button> _actions = [];
    private readonly Dictionary<string, TextBlock> _serviceRows = [];
    private readonly DispatcherTimer _serviceTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly CancellationTokenSource _serviceChecks = new();
    private readonly bool _healthEnabled;
    private bool _serviceProbeBusy;
    private TextBlock? _serviceChecked;

    public MainWindow() : this(new AppViewModel(), true) { }

    internal MainWindow(AppViewModel vm, bool initializeBackend)
    {
        _vm = vm;
        _healthEnabled = initializeBackend;
        InitializeComponent();
        DataContext = _vm;
        CloseButton.Click += (_, _) => Close();
        MinimizeButton.Click += (_, _) => WindowState = WindowState.Minimized;
        BoundedWindow.Apply(this);
        RoomNav.Click += (_, _) => Navigate("Room");
        AdaptersNav.Click += (_, _) => Navigate("Connections");
        DiagnosticsNav.Click += (_, _) => Navigate("Diagnostics");
        ChatNav.Click += (_, _) => Navigate("Chat");
        DevNav.Click += (_, _) => Navigate("Developer");
        HelpButton.Click += (_, _) => Navigate("Guide");
        SizeChanged += (_, e) =>
        {
            var compact = e.NewSize.Height < 700;
            if (compact == _compact) return;
            _compact = compact;
            RefreshScreen();
        };
        _vm.PropertyChanged += OnModelChanged;
        _vm.ConfirmInternetAdapter = name => MessageBox.Show(
            this,
            $"'{name}' is carrying this PC's Internet connection."
            + Environment.NewLine + Environment.NewLine
            + "CODCONNECT takes the console port over completely, so this PC would lose its Internet "
            + "connection and your home network's traffic could reach the room."
            + Environment.NewLine + Environment.NewLine
            + "Use it anyway?",
            "Use the Internet adapter?",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning,
            MessageBoxResult.No) == MessageBoxResult.Yes;
        _serviceTimer.Tick += async (_, _) => await RefreshServiceHealthAsync();
        Closed += (_, _) => { _vm.PropertyChanged -= OnModelChanged; _vm.StopPolling(); _serviceTimer.Stop(); _serviceChecks.Cancel(); if (initializeBackend) ChatToasts.Clear(); };
        Activated += (_, _) =>
        {
            if (_page == "Chat") ClearUnreadChat();
            if (initializeBackend) CheckPrerequisites();
        };
        if (initializeBackend)
        {
            _vm.IncomingChat += message => Dispatcher.BeginInvoke(() => OnIncomingChat(message));
            ChatToasts.Activated = () => Dispatcher.BeginInvoke(OpenChatFromNotification);
        }
        _initialized = !initializeBackend;
        Navigate("Room");
        RefreshScreen();
        if (initializeBackend) _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try { await _vm.InitializeAsync(); }
        catch (Exception ex) { _startupError = ex.Message; }
        finally { _initialized = true; RefreshChrome(); if (_page == "Connections") ShowAdapters(); }
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) => Dispatcher.Invoke(() =>
    {
        if (e.PropertyName == nameof(AppViewModel.BackendMode) && _page == "Guide" && _troubleshooting)
            Navigate("Guide");
        if (_shownScreen != _vm.Screen
            || (e.PropertyName == nameof(AppViewModel.WifiCapability) && _vm.IsHome)
            || (e.PropertyName == nameof(AppViewModel.WifiNetwork) && !_vm.IsHome)) RefreshScreen();
        RefreshChrome();
        if (e.PropertyName == nameof(AppViewModel.Chat)) RefreshChat();
        if (_page == "Connections" && e.PropertyName is nameof(AppViewModel.WifiNetwork) or nameof(AppViewModel.WifiCapability)) ShowAdapters();
    });

    private Brush Color(string key) => (Brush)FindResource(key);
    private TextBlock Text(string value, bool muted = false, double size = 13) => new()
    { Text = value, Style = (Style)FindResource(muted ? "Muted" : "BaseText"), FontSize = size };
    private static void Space(FrameworkElement element, double top = 0, double bottom = 0) => element.Margin = new Thickness(0, top, 0, bottom);
    private void Divider(Panel panel, double margin = 22) => panel.Children.Add(new Border { Height = 1, Background = Color("LineBrush"), Margin = new Thickness(0, margin, 0, margin) });
    private Button Button(string label, Action action, bool primary = false)
    {
        var button = new Button { Content = label, Style = (Style)FindResource(primary ? "PrimaryButton" : "GhostButton") };
        button.Click += (_, _) => action();
        return button;
    }
    private Button ActionButton(string label, Func<Task> action, bool primary = false)
    {
        var button = Button(label, async () => await RunAsync(action), primary);
        _actions.Add(button);
        return button;
    }

    private void RefreshChrome()
    {
        foreach (var action in _actions) action.IsEnabled = _initialized && !_vm.IsBusy;
        foreach (var choice in DetailContent.Children.OfType<RadioButton>()) choice.IsEnabled = _vm.IsHome && !_vm.IsBusy;
        NetworkBadge.Text = RoomBadge.Describe(_vm.Screen, _vm.Players, _vm.RoomEvent);
        NetworkBadge.Foreground = Color(_vm.Screen == Screen.Connected && (_vm.Players.Count == 0 || _vm.Players.Any(p => p.Connected)) ? "AccentBrush" : "MutedBrush");
        if (_engine is not null)
            _engine.Text = !_initialized ? "Starting…" : _vm.BackendMode == "service" ? "Background service" : "Desktop app";
        Diagram.State = _vm.Visual;
        if (_roomCode is not null) _roomCode.Text = _vm.RoomCode;
        if (_localConsoleRow is not null) _localConsoleRow.Text = _vm.LocalConsoleIdentity ?? "Not identified";
        if (_remoteConsoleRow is not null) _remoteConsoleRow.Text = _vm.RemoteConsoleIdentity ?? "Not identified";
        RefreshPlayerRows();
        if (_connectionRow is not null) _connectionRow.Text = _vm.ConnectionKind ?? (_vm.Screen == Screen.Connected ? "Direct" : "Pending");
        if (_internetRow is not null) _internetRow.Text = DescribeConsoleInternet(_vm.ConsoleInternet);
        if (_error is not null)
        {
            var message = _startupError ?? _vm.ErrorText;
            _error.Text = message.Length > 110 ? message[..107] + "…" : message;
            _error.ToolTip = new TextBlock { Text = message, MaxWidth = 400, TextWrapping = TextWrapping.Wrap };
            AutomationProperties.SetHelpText(_error, message);
            _error.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_status is not null) _status.Text = string.IsNullOrEmpty(_vm.StatusText) ? "No active session" : _vm.StatusText;
        if (_counters is not null) _counters.Text = string.IsNullOrEmpty(_vm.CountersText) ? "No traffic recorded" : _vm.CountersText;
        CanvasHint.Visibility = Visibility.Collapsed;
    }

    private void RefreshScreen()
    {
        _shownScreen = _vm.Screen;
        _actions.Clear(); _roomCode = null; _localConsoleRow = null; _remoteConsoleRow = null; _connectionRow = null; _internetRow = null; _playerRows = null;
        if (_vm.Screen == Screen.Home) ShowHome(); else ShowSession();
        RefreshChrome();
    }

    private void ShowHome()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text("Console connection", size: 18));
        var choices = new Grid { Margin = new Thickness(0, _compact ? 12 : 20, 0, 0) };
        choices.ColumnDefinitions.Add(new ColumnDefinition()); choices.ColumnDefinitions.Add(new ColumnDefinition());
        var capability = _vm.WifiCapability;
        var wifiAvailable = capability?.Supported == true;
        if (!wifiAvailable) _vm.WifiMode = false;
        var ethernet = new RadioButton { Content = "Ethernet", GroupName = "ConsoleMode", IsChecked = !_vm.WifiMode, Style = (Style)FindResource("Choice"), Margin = new Thickness(0, 0, 4, 0) };
        var wifiReason = capability is null ? "Checking Wi-Fi…" : capability.Reason;
        var wifi = new RadioButton { Content = "Wi-Fi", GroupName = "ConsoleMode", IsChecked = _vm.WifiMode, IsEnabled = wifiAvailable, Style = (Style)FindResource("Choice"), Margin = new Thickness(4, 0, 0, 0), ToolTip = wifiReason };
        AutomationProperties.SetHelpText(wifi, wifiReason);
        ToolTipService.SetShowOnDisabled(wifi, true);
        ethernet.Checked += (_, _) => { _vm.WifiMode = false; RefreshScreen(); };
        wifi.Checked += (_, _) => { _vm.WifiMode = true; RefreshScreen(); };
        Grid.SetColumn(wifi, 1); choices.Children.Add(ethernet); choices.Children.Add(wifi); panel.Children.Add(choices);
        var setup = Text(_vm.WifiMode
            ? "Your console joins a Wi-Fi network this PC creates."
            : "Plug your console into a spare Ethernet port on this PC.", true);
        setup.TextWrapping = TextWrapping.Wrap; Space(setup, 8); panel.Children.Add(setup);
        if (NpcapMissing == true)
            MissingNotice(panel, "CODCONNECT needs Npcap to reach your console. Install it, then come back.", "Get Npcap", "https://npcap.com/#download");
        if (SoftEtherClientMissing == true)
        {
            // CODCONNECT ships SoftEther's own client setup; it creates the network adapter rooms use.
            var bundled = Path.Combine(AppContext.BaseDirectory, "prereqs", "softether-vpnclient-installer.exe");
            MissingNotice(panel, "CODCONNECT needs the SoftEther VPN Client to connect rooms.", "Install SoftEther VPN Client",
                File.Exists(bundled) ? bundled : "https://www.softether-download.com/");
        }

        if (NpcapMissing == true || SoftEtherClientMissing == true)
        {
            // Rooms cannot work without these, so the room controls wait until they are installed.
            ScreenHost.Content = panel;
            return;
        }

        Divider(panel, _compact ? 12 : 20);
        var tabs = new Grid(); tabs.ColumnDefinitions.Add(new ColumnDefinition()); tabs.ColumnDefinitions.Add(new ColumnDefinition());
        var hostTab = Button("Create room", () => { _joining = false; RefreshScreen(); });
        var joinTab = Button("Join room", () => { _joining = true; RefreshScreen(); });
        hostTab.Background = _joining ? Brushes.Transparent : Color("PanelLightBrush");
        joinTab.Background = _joining ? Color("PanelLightBrush") : Brushes.Transparent;
        hostTab.Margin = new Thickness(0, 0, 4, 0); joinTab.Margin = new Thickness(4, 0, 0, 0);
        hostTab.IsEnabled = joinTab.IsEnabled = !_vm.IsBusy;
        _actions.Add(hostTab); _actions.Add(joinTab);
        Grid.SetColumn(joinTab, 1); tabs.Children.Add(hostTab); tabs.Children.Add(joinTab); panel.Children.Add(tabs);
        var label = Text("Your name", true, 11); Space(label, _compact ? 12 : 20, 6); panel.Children.Add(label);
        var name = new TextBox { Text = _vm.DisplayName, MaxLength = 12 }; AutomationProperties.SetName(name, "Your name");
        name.TextChanged += (_, _) => { _vm.DisplayName = name.Text; RefreshChrome(); }; panel.Children.Add(name);
        TextBox? code = null;
        if (_joining)
        {
            var codeLabel = Text("Room code", true, 11); Space(codeLabel, _compact ? 12 : 16, 6); panel.Children.Add(codeLabel);
            code = new TextBox { Text = _joinCode, MaxLength = 7, CharacterCasing = CharacterCasing.Upper };
            AutomationProperties.SetName(code, "Room code"); code.TextChanged += (_, _) => _joinCode = code.Text; panel.Children.Add(code);
        }
        var submit = ActionButton(_joining ? "Join room" : "Create room", () => _joining ? _vm.JoinRoomAsync(NormalizeCode(_joinCode)) : _vm.CreateRoomAsync(), true);
        AutomationProperties.SetAutomationId(submit, "RoomSubmit");
        Space(submit, _compact ? 12 : 20); panel.Children.Add(submit);
        AddError(panel);
        var options = Button("Connection options", () => Navigate("Connections"));
        Space(options, _compact ? 16 : 24); panel.Children.Add(options);
        ScreenHost.Content = panel;
    }

    private void ShowSession()
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(_vm.Screen == Screen.Connected ? "Room connected" : _vm.Screen == Screen.Hosting ? "Your room is open" : "Connecting to room", size: 18));
        var codeLabel = Text("Room code", true, 11); Space(codeLabel, _compact ? 16 : 24); panel.Children.Add(codeLabel);
        _roomCode = Text(_vm.RoomCode, size: 32); _roomCode.Style = (Style)FindResource("Mono"); Space(_roomCode, 8, 14); panel.Children.Add(_roomCode);
        Button? copy = null;
        copy = Button("Copy room code", () =>
        {
            try { Clipboard.SetText(_vm.RoomCode); copy!.Content = "Copied"; }
            catch { _error!.Text = "Couldn’t copy. Share the code shown above."; _error.Visibility = Visibility.Visible; }
        });
        panel.Children.Add(copy); Divider(panel, _compact ? 12 : 22);
        if (_vm.WifiNetwork is { } network)
        {
            var join = Text("On your console, join", true, 11); Space(join, 0, 8); panel.Children.Add(join);
            AccentRow(panel, "Network", network.Ssid);
            AccentRow(panel, "Password", network.Passphrase);
            Divider(panel, _compact ? 12 : 22);
        }
        _connectionRow = AddRow(panel, "Connection", _vm.ConnectionKind ?? (_vm.Screen == Screen.Connected ? "Direct" : "Pending"));
        _localConsoleRow = AddRow(panel, "Your console", _vm.LocalConsoleIdentity ?? "Not identified");
        _playerRows = new StackPanel(); _playerRowsShown = "";
        panel.Children.Add(_playerRows);
        RefreshPlayerRows();
        _internetRow = AddRow(panel, "Console Internet", DescribeConsoleInternet(_vm.ConsoleInternet));
        Divider(panel, _compact ? 12 : 22);
        panel.Children.Add(Text(_vm.Screen == Screen.Connected ? "Open your game’s LAN menu." : "Share this code with your friend.", true));
        var leave = ActionButton("Leave room", () => _vm.DisconnectAsync()); Space(leave, _compact ? 16 : 24); panel.Children.Add(leave);
        AddError(panel); ScreenHost.Content = panel;
    }

    private void AddError(Panel panel)
    {
        _error = Text(_vm.ErrorText, size: 12); _error.Foreground = Color("WarnBrush");
        _error.MaxHeight = 60;
        _error.TextTrimming = TextTrimming.WordEllipsis;
        AutomationProperties.SetLiveSetting(_error, AutomationLiveSetting.Assertive);
        Space(_error, 12); panel.Children.Add(_error);
    }

    private void AccentRow(Panel panel, string label, string value)
    {
        var row = AddRow(panel, label, value);
        row.Style = (Style)FindResource("Mono");
        row.FontSize = 15;
        row.Foreground = Color("AccentBrush");
        AutomationProperties.SetName(row, $"{label}: {value}");
    }

    private TextBlock AddRow(Panel panel, string label, string value)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, _compact ? 10 : 14) };
        row.ColumnDefinitions.Add(new ColumnDefinition()); row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(Text(label, true, 12)); var text = Text(value, size: 12); Grid.SetColumn(text, 1); row.Children.Add(text); panel.Children.Add(row);
        return text;
    }

    private void Navigate(string page)
    {
        _page = page;
        _serviceTimer.Stop(); _serviceRows.Clear(); _serviceChecked = null;
        foreach (var (button, name) in new[] { (RoomNav, "Room"), (AdaptersNav, "Connections"), (DiagnosticsNav, "Diagnostics"), (ChatNav, "Chat"), (DevNav, "Developer"), (HelpButton, "Guide") })
        {
            button.Background = page == name ? Color("PanelLightBrush") : Brushes.Transparent;
            button.Foreground = Color(page == name ? "AccentBrush" : "MutedBrush");
        }
        NetworkView.Visibility = page == "Room" ? Visibility.Visible : Visibility.Collapsed;
        DetailView.Visibility = page == "Room" ? Visibility.Collapsed : Visibility.Visible;
        PageTitle.Text = page switch { "Room" => "Your network", "Guide" => "Help", "Chat" => "Room chat", "Connections" => "Console", "Developer" => "Optional features", _ => page };
        _status = _counters = _engine = null;
        DetailContent.Children.Clear();
        if (page == "Connections") ShowAdapters();
        if (page == "Developer") ShowDeveloper();
        if (page == "Diagnostics")
        {
            DetailContent.Children.Add(Text("Services", size: 16));
            _serviceChecked = Text(_healthEnabled ? "Checking…" : "Not checked", true, 11); Space(_serviceChecked, 6, 18); DetailContent.Children.Add(_serviceChecked);
            foreach (var name in new[] { "Codconnect", "Npcap driver", "SoftEther client" })
                _serviceRows[name] = AddRow(DetailContent, name, "Not checked");
            _serviceRows["Service response"] = AddRow(DetailContent, "Service response", "Not checked");
            _serviceRows["SoftEther client"].ToolTip = "Needed for encrypted, NAT-traversing rooms; without it CODCONNECT falls back to direct TCP (same LAN only). This row checks the SoftEther VPN Client service.";
            _serviceRows["Npcap driver"].ToolTip = "Windows driver status, not proof that console capture is working.";
            _serviceRows["Service response"].ToolTip = "A separate ping checks whether the Codconnect service responds; Running alone does not prove that.";
            _engine = AddRow(DetailContent, "Connection engine", "");
            Divider(DetailContent, 14);
            DetailContent.Children.Add(Text("Session", size: 16)); _status = Text("", true); Space(_status, 8); DetailContent.Children.Add(_status);
            Divider(DetailContent, 14); DetailContent.Children.Add(Text("Packet activity", size: 16)); _counters = Text("", true); Space(_counters, 8); DetailContent.Children.Add(_counters);
            if (_healthEnabled) { _serviceTimer.Start(); _ = RefreshServiceHealthAsync(); }
        }
        if (page == "Guide") ShowHelp();
        if (page == "Chat") ShowChat();
        RefreshChrome();
    }

    private void MissingNotice(Panel panel, string message, string action, string target)
    {
        var missing = Text(message, size: 12);
        missing.Foreground = Color("WarnBrush"); Space(missing, 12, 8); panel.Children.Add(missing);
        var get = Button(action, () =>
        {
            try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
            catch (Win32Exception) { }
        });
        get.HorizontalAlignment = HorizontalAlignment.Left; panel.Children.Add(get);
    }

    /// <summary>What CODCONNECT cannot run without, rechecked whenever the window comes back to the front.</summary>
    private void CheckPrerequisites()
    {
        var npcap = ServiceHealth.ReadState("npcap") == ServiceRunState.Missing;
        var softEther = ServiceHealth.ReadState("SEVPNCLIENT") == ServiceRunState.Missing;
        if (npcap == NpcapMissing && softEther == SoftEtherClientMissing) return;
        NpcapMissing = npcap;
        SoftEtherClientMissing = softEther;
        if (_vm.IsHome) RefreshScreen();
    }

    private void RefreshPlayerRows()
    {
        if (_playerRows is null) return;
        List<(string Name, string Value)> rows = _vm.Players.Count switch
        {
            0 => [("Friend’s console", _vm.RemoteConsoleIdentity ?? "Not identified")],
            1 => [(_vm.Players[0].Name, RoomBadge.PlayerDetail(_vm.Players[0]))],
            _ => [("Friends", RoomBadge.FriendsSummary(_vm.Players))],
        };
        var shown = string.Join("\n", rows.Select(r => r.Name + "\t" + r.Value));
        if (shown == _playerRowsShown) return;
        _playerRowsShown = shown;
        _playerRows.Children.Clear();
        _remoteConsoleRow = null;
        foreach (var (name, value) in rows)
        {
            var row = AddRow(_playerRows, name, value);
            if (_vm.Players.Count == 0) _remoteConsoleRow = row;
        }
    }

    private async Task RefreshServiceHealthAsync()
    {
        if (!_healthEnabled || _serviceProbeBusy || _page != "Diagnostics" || WindowState == WindowState.Minimized || _serviceChecks.IsCancellationRequested) return;
        _serviceProbeBusy = true;
        if (_serviceChecked is not null) _serviceChecked.Text = "Checking…";
        try { ApplyServiceHealth(await ServiceHealth.ReadAsync(_serviceChecks.Token)); }
        catch (OperationCanceledException) { }
        catch (Exception)
        {
            if (_page == "Diagnostics")
            {
                foreach (var row in _serviceRows.Values) { row.Text = "Status unavailable"; row.Foreground = Color("MutedBrush"); }
                if (_serviceChecked is not null) _serviceChecked.Text = "Couldn’t check services";
            }
        }
        finally { _serviceProbeBusy = false; }
    }

    internal void ApplyServiceHealth(ServiceHealthSnapshot snapshot)
    {
        if (_page != "Diagnostics") return;
        foreach (var (name, state) in new[] { ("Codconnect", snapshot.Codconnect), ("Npcap driver", snapshot.Npcap), ("SoftEther client", snapshot.SoftEther) })
        {
            _serviceRows[name].Text = ServiceHealth.Describe(state);
            _serviceRows[name].Foreground = Color(state == ServiceRunState.Running ? "AccentBrush" : "MutedBrush");
        }
        _serviceRows["Service response"].Text = snapshot.Reply switch
        {
            ServiceReply.Responding => "Responding", ServiceReply.NoResponse => "No response",
            ServiceReply.AccessDenied => "Access denied",
            ServiceReply.Error => "Returned an error", _ => "Not checked"
        };
        _serviceRows["Service response"].Foreground = Color(snapshot.Reply == ServiceReply.Responding ? "AccentBrush" : "MutedBrush");
        if (_serviceChecked is not null) _serviceChecked.Text = $"Checked {snapshot.CheckedAt.ToLocalTime():HH:mm:ss} · refreshes every 5 s";
    }

    private void ShowAdapters()
    {
        DetailContent.Children.Clear();
        var wifi = _vm.IsHome ? _vm.WifiMode : _vm.WifiNetwork is not null;
        if (wifi)
        {
            ShowWifiPort();
            return;
        }

        DetailContent.Children.Add(Text("Console port", size: 16));
        var explanation = Text("The Ethernet port your console is plugged into.", true);
        explanation.TextWrapping = TextWrapping.Wrap; Space(explanation, 8, 20); DetailContent.Children.Add(explanation);
        AddAdapterChoice("Automatic", "Uses the first Ethernet port with a cable in it", "");

        var candidates = _vm.Adapters.Where(a => a.Kind == "Ethernet" && !a.CarriesInternet && !a.IsLoopback)
            .OrderByDescending(a => a.LinkState == "Up").ToList();
        var shown = _showAllAdapters
            ? _vm.Adapters.OrderByDescending(a => candidates.Contains(a)).ThenBy(a => a.CarriesInternet).ThenBy(a => a.Kind == "Virtual").ToList()
            : candidates;

        const int pageSize = 3;
        var pages = Math.Max(1, (shown.Count + pageSize - 1) / pageSize);
        _adapterPage = Math.Clamp(_adapterPage, 0, pages - 1);
        foreach (var adapter in shown.Skip(_adapterPage * pageSize).Take(pageSize))
            AddAdapterChoice(adapter.Name, DescribeAdapter(adapter), adapter.Index.ToString(), adapter.CarriesInternet);

        if (candidates.Count == 0 && !_showAllAdapters)
        {
            var none = Text(_vm.Adapters.Count == 0
                ? "No network adapters found. Check Diagnostics."
                : "No spare Ethernet port found. Use Wi-Fi, or add a USB Ethernet adapter.", true);
            none.TextWrapping = TextWrapping.Wrap; Space(none, 4, 12); DetailContent.Children.Add(none);
        }

        var footer = new Grid { Margin = new Thickness(0, 8, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition()); footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var toggle = Button(_showAllAdapters ? "Show Ethernet ports only" : "Show all adapters", () => { _showAllAdapters = !_showAllAdapters; _adapterPage = 0; ShowAdapters(); });
        toggle.HorizontalAlignment = HorizontalAlignment.Left; footer.Children.Add(toggle);
        if (pages > 1)
        {
            var pager = new StackPanel { Orientation = Orientation.Horizontal };
            var previous = Button("←", () => { _adapterPage--; ShowAdapters(); }); previous.IsEnabled = _adapterPage > 0;
            AutomationProperties.SetName(previous, "Previous adapters");
            var count = Text($"{_adapterPage + 1} / {pages}", true); count.VerticalAlignment = VerticalAlignment.Center; count.Margin = new Thickness(12, 0, 12, 0);
            var next = Button("→", () => { _adapterPage++; ShowAdapters(); }); next.IsEnabled = _adapterPage < pages - 1;
            AutomationProperties.SetName(next, "Next adapters");
            pager.Children.Add(previous); pager.Children.Add(count); pager.Children.Add(next);
            Grid.SetColumn(pager, 1); footer.Children.Add(pager);
        }

        DetailContent.Children.Add(footer);
    }

    private void ShowWifiPort()
    {
        DetailContent.Children.Add(Text("Console Wi-Fi", size: 16));
        var explanation = Text("This PC creates a Wi-Fi network for your console. There is nothing to choose.", true);
        explanation.TextWrapping = TextWrapping.Wrap; Space(explanation, 8, 20); DetailContent.Children.Add(explanation);
        var capability = _vm.WifiCapability;
        AddRow(DetailContent, "Wi-Fi adapter", capability?.Adapter ?? "Not found");
        AddRow(DetailContent, "Band", capability?.Band is { } linkBand ? $"{linkBand}, same as this PC’s Wi-Fi" : _vm.WifiNetwork?.Band ?? "2.4 GHz");
        if (_vm.WifiNetwork is { } network)
        {
            AccentRow(DetailContent, "Network", network.Ssid);
            AccentRow(DetailContent, "Password", network.Passphrase);
        }
        else
        {
            var later = Text("The network name and password appear when your room starts.", true);
            later.TextWrapping = TextWrapping.Wrap; Space(later, 6); DetailContent.Children.Add(later);
        }
    }

    private static string DescribeConsoleInternet(string? state) => state switch
    {
        "Ready" => "Through this PC",
        "Starting" => "Setting up…",
        "Unavailable" => "Unavailable",
        _ => "Off",
    };

    private static string DescribeAdapter(IpcAdapterInfo adapter)
    {
        if (adapter.CarriesInternet) return "Carries this PC's Internet";
        if (adapter.IsLoopback) return "Loopback capture";
        if (adapter.Kind == "Virtual") return "Virtual adapter";
        if (adapter.Kind == "Ethernet") return adapter.LinkState == "Up" ? "Cable connected" : "No cable";
        return adapter.LinkState == "Up" ? "Connected" : "Not connected";
    }

    private void AddAdapterChoice(string name, string description, string selector, bool carriesInternet = false)
    {
        var content = new StackPanel();
        var title = Text(name); title.TextWrapping = TextWrapping.NoWrap; title.TextTrimming = TextTrimming.CharacterEllipsis; title.ToolTip = name; content.Children.Add(title);
        var detail = Text(description, true, 11); detail.TextWrapping = TextWrapping.NoWrap; detail.TextTrimming = TextTrimming.CharacterEllipsis; detail.ToolTip = description; Space(detail, 4);
        if (carriesInternet) detail.Foreground = Color("WarnBrush");
        content.Children.Add(detail);
        var choice = new RadioButton { GroupName = "Adapters", Content = content, Style = (Style)FindResource("Choice"), IsChecked = _vm.AdapterSelection == selector, IsEnabled = _vm.IsHome && !_vm.IsBusy, Margin = new Thickness(0, 0, 0, 8) };
        choice.Checked += (_, _) => _vm.AdapterSelection = selector;
        DetailContent.Children.Add(choice);
    }

    internal static string NormalizeCode(string input)
    {
        var compact = new string(input.Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        return compact.Length == 6 ? $"{compact[..4]}-{compact[4..]}" : compact;
    }

    private async Task RunAsync(Func<Task> action)
    {
        if (_vm.IsBusy || !_initialized) return;
        await action();
        RefreshChrome();
        if (_page == "Connections") ShowAdapters();
    }
}
