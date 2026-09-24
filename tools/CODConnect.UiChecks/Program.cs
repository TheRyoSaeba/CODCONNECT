using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Automation;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Interop;
using System.Windows.Threading;
using CODConnect.UI;
using CODConnect.Protocol;

internal static class Program
{
    private static int _checks;
    [STAThread]
    private static void Main(string[] args)
    {
        if (args.Contains("--service-health"))
        {
            var health = ServiceHealth.ReadAsync(CancellationToken.None).GetAwaiter().GetResult();
            Console.WriteLine($"Codconnect: {health.Codconnect}; Npcap: {health.Npcap}; SoftEther: {health.SoftEther}; IPC: {health.Reply}; Checked: {health.CheckedAt:O}");
            return;
        }
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/ui-review/screenshots");
        Directory.CreateDirectory(output);
        var app = new App(); app.InitializeComponent();
        var vm = new AppViewModel();
        vm.Adapters.Add(new IpcAdapterInfo(0, "Ethernet · console", "02:00:00:00:00:01", ["192.168.1.20"], "Up", false, Kind: "Ethernet"));
        vm.Adapters.Add(new IpcAdapterInfo(1, "USB Ethernet adapter with a deliberately long device name", "02:00:00:00:00:02", [], "Down", false, Kind: "Ethernet"));
        var window = new MainWindow(vm, false) { Left = -20000, Top = -20000, ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual };
        window.Show();
        window.WindowState = WindowState.Maximized;
        Check(window.WindowState == WindowState.Normal, "Maximize requests are rejected");
        Check(window.FindName("MaximizeButton") is null, "No maximize control");
        var handle = new WindowInteropHelper(window).Handle;
        Check((GetWindowLong(handle, -16) & 0x00010000) == 0, "Native maximize style removed");
        SendMessage(handle, 0x0112, 0xF030, 0);
        Check(window.WindowState == WindowState.Normal, "Native maximize command blocked");
        Render(window, output, "max-size", 1600, 1000);
        Check(window.ActualWidth <= 1280 && window.ActualHeight <= 720, "Resize capped at 1280 by 720");
        Render(window, output, "home", 1180, 720);
        Check(Contains(window, "Console connection"), "Home content");
        Check(window.Title == "CODConnect" && !Descendants<TextBlock>(window).Any(t => t.Text.Contains("preview", StringComparison.OrdinalIgnoreCase) || t.Text.Contains("artifact", StringComparison.OrdinalIgnoreCase)), "Product UI has no artifact or preview labels");
        Click(window, "Join room");
        Render(window, output, "join-small", 900, 620);
        Check(Descendants<TextBox>(window).Count() == 2, "Join has name and code inputs");
        Descendants<TextBox>(window).Single(t => AutomationProperties.GetName(t) == "Room code").Text = "K7M4-P2";
        Render(window, output, "join", 1180, 720);
        Check(Descendants<TextBox>(window).Single(t => AutomationProperties.GetName(t) == "Room code").Text == "K7M4-P2", "Room code retained across responsive layout change");
        Check(!Descendants<RadioButton>(window).Single(r => Equals(r.Content, "Wi-Fi")).IsEnabled, "Wi-Fi control reflects unavailable backend");
        Check(MainWindow.NormalizeCode(" k7m4p2 ") == "K7M4-P2", "Room code normalization");
        vm.DisplayName = " "; vm.CreateRoomAsync().GetAwaiter().GetResult();
        Check(Contains(window, "Enter your name to continue."), "Validation errors visible without rebuilding view");
        vm.DisplayName = "Player"; vm.JoinRoomAsync("bad").GetAwaiter().GetResult();
        Check(vm.ErrorText.Contains("format"), "Invalid code rejected before backend call");
        Render(window, output, "validation", 1180, 760);
        Set(vm, "ErrorText", new string('x', 1400)); Notify(vm, "ErrorText");
        Render(window, output, "long-error-max", 1280, 720);
        Render(window, output, "long-error-small", 900, 620);
        Set(vm, "ErrorText", ""); Notify(vm, "ErrorText");
        const string step = "Setting up CODCONNECT’s network adapter. This only happens the first time…";
        Set(vm, "IsBusy", true); Set(vm, "ProgressText", step); Notify(vm, "ProgressText");
        Render(window, output, "progress-small", 900, 620);
        var stepText = Descendants<TextBlock>(window).SingleOrDefault(t => t.Text == step && t.IsVisible);
        var roomHost = (FrameworkElement)window.FindName("ScreenHost");
        var optionsButton = Descendants<Button>(roomHost).Single(b => Equals(b.Content, "Connection options"));
        Check(stepText is not null
              && stepText.TransformToAncestor(roomHost).Transform(new Point()).Y > optionsButton.TransformToAncestor(roomHost).TransformBounds(new Rect(optionsButton.RenderSize)).Bottom
              && stepText.TransformToAncestor(roomHost).TransformBounds(new Rect(stepText.RenderSize)).Bottom <= roomHost.ActualHeight + 1,
            "Progress shows below Connection options and stays on screen");
        Set(vm, "IsBusy", false); Set(vm, "ProgressText", ""); Notify(vm, "IsBusy");
        Click(window, "Connection options");
        Check(Contains(window, "Console port"), "Connection navigation");
        var adapters = Descendants<RadioButton>(window).Where(r => r.GroupName == "Adapters").ToArray();
        adapters[2].IsChecked = true;
        Check(vm.AdapterSelection == "1", "Explicit adapter index reaches view model");
        Render(window, output, "adapters", 1180, 760);
        for (var i = 2; i < 14; i++) vm.Adapters.Add(new IpcAdapterInfo(i, new string('W', 150) + i, "02:00:00:00:00:03", ["192.168.1.21", "fe80::abcd:ef12:3456:7890"], "Up", false, Kind: "Ethernet"));
        ClickNamed(window, "AdaptersNav");
        Render(window, output, "adapters-many-small", 900, 620);
        Check(Descendants<RadioButton>(window).Count(r => r.GroupName == "Adapters") == 4, "Adapter page is bounded");
        for (var i = 0; i < 4; i++) { Click(window, "→"); window.UpdateLayout(); }
        Descendants<RadioButton>(window).Where(r => r.GroupName == "Adapters").Skip(1).First().IsChecked = true;
        Check(vm.AdapterSelection == "13", "Last adapter reachable through pagination");
        Render(window, output, "adapters-last-small", 900, 620);
        vm.DevSettingsFixture = new IpcDevSettings(false, false, "https://codconnect-rendezvous.onrender.com/", "https://codconnect-rendezvous.onrender.com/");
        ClickNamed(window, "DevNav"); Pump(50); Render(window, output, "developer", 1180, 760); Render(window, output, "developer-small", 900, 620);
        Check(Contains(window, "Optional features") && Contains(window, "Let friends FTP to my console") && Contains(window, "Always join through the relay") && Contains(window, "Room server"), "Optional features tab");
        Check(Descendants<TextBox>(window).Single(t => AutomationProperties.GetName(t) == "Room server address").IsEnabled == false, "Room server locked until confirmed");
        vm.DevSettingsFixture = new IpcDevSettings(true, true, "https://rooms.example.org/", "https://codconnect-rendezvous.onrender.com/");
        ClickNamed(window, "DevNav"); Pump(50); Render(window, output, "developer-custom-small", 900, 620);
        Check(Contains(window, "You are using your own room server. Friends must use the same one to find your rooms."), "Custom room server is called out");
        ClickNamed(window, "DiagnosticsNav"); Render(window, output, "diagnostics", 1180, 760);
        window.ApplyServiceHealth(new(ServiceRunState.Running, ServiceRunState.Running, ServiceRunState.Missing, ServiceReply.Responding, DateTimeOffset.Now));
        Render(window, output, "diagnostics-small", 900, 620);
        Check(Contains(window, "Responding") && Contains(window, "Not installed"), "Actual service and reply states are separate");
        window.ApplyServiceHealth(new(ServiceRunState.Running, ServiceRunState.Stopped, ServiceRunState.AccessDenied, ServiceReply.NoResponse, DateTimeOffset.Now));
        Render(window, output, "diagnostics-no-response-small", 900, 620);
        Check(Contains(window, "Running") && Contains(window, "No response"), "Running does not imply responding");
        Check(ServiceHealth.ReadState("test", () => throw new InvalidOperationException("missing", new System.ComponentModel.Win32Exception(1060))) == ServiceRunState.Missing, "Absent SCM service maps to not installed");
        Check(ServiceHealth.ReadState("test", () => throw new System.ComponentModel.Win32Exception(5)) == ServiceRunState.AccessDenied, "Denied SCM access is not missing");
        Check(ServiceHealth.ReadState("test", () => System.ServiceProcess.ServiceControllerStatus.StopPending) == ServiceRunState.Stopping, "Pending service status stays distinct");
        Check(Contains(window, "No traffic recorded"), "Diagnostics empty state");
        ClickNamed(window, "HelpButton"); Render(window, output, "guide-small", 900, 620);
        Check(Contains(window, "Plug in your console"), "Guide navigation");
        Click(window, "Wi-Fi"); Render(window, output, "guide-wifi-small", 900, 620);
        Check(Contains(window, "Connect your console"), "Wi-Fi setup steps");
        Click(window, "Troubleshooting"); Render(window, output, "trouble-console-small", 900, 620);
        Check(Contains(window, "Your console isn’t detected"), "Troubleshooting opens on the likeliest problem");
        Click(window, "Windows Firewall"); Render(window, output, "firewall-service-small", 900, 620);
        Check(Contains(window, "Background service"), "Service firewall guidance");
        Set(vm, "BackendMode", "embedded"); Notify(vm, "BackendMode");
        Render(window, output, "firewall-local-max", 1280, 720);
        Check(Contains(window, "Desktop app"), "Embedded firewall guidance");
        const string renamedService = @"C:\Program Files\New name\Network.exe";
        const string renamedApp = @"D:\Games\Connect.exe";
        Check(FirewallHelp.ResolveExecutable("service", $"\"{renamedService}\" --service", renamedApp, _ => true) == renamedService, "Service path follows registered installation and rename");
        Check(FirewallHelp.ResolveExecutable("embedded", null, renamedApp, _ => true) == renamedApp, "Embedded path follows running executable");
        Check(FirewallHelp.ResolveExecutable("service", null, renamedApp, _ => true) is null, "Missing service never suggests desktop executable");
        Check(FirewallHelp.ResolveExecutable("service", renamedService, renamedApp, _ => false) is null, "Missing executable has no copyable path");
        Check(FirewallHelp.ResolveExecutable("embedded", null, @"C:\dotnet\dotnet.exe", _ => true) is null, "Shared development host is not suggested");
        Check(FirewallHelp.ParseExecutablePath("\"C:\\broken.exe") is null, "Malformed registered command rejected");
        Set(vm, "BackendMode", "service");
        Click(window, "Setup"); Check(Contains(window, "Get ready to play"), "Return from troubleshooting");
        ClickNamed(window, "RoomNav");
        Set(vm, "ErrorText", ""); Set(vm, "RoomCode", "K7M4-P2"); Set(vm, "Screen", Screen.Hosting);
        Check(Contains(window, "K7M4-P2"), "State notification switches rendered screen");
        Render(window, output, "hosting", 1180, 760);
        Set(vm, "Screen", Screen.Connecting); Render(window, output, "connecting", 1180, 760);
        Check(Contains(window, "Connecting to friend"), "Connecting friend badge");
        Set(vm, "Screen", Screen.Connected); Render(window, output, "connected", 1180, 760);
        Check(Contains(window, "Open your game’s LAN menu."), "Poll-driven connected screen");
        Check(Contains(window, "Friend joined") && !Contains(window, "LAN ready"), "Live badge is friend state and never LAN state");
        Render(window, output, "connected-small", 900, 620);
        var now = DateTimeOffset.Now;
        var history = new RoomChatSnapshot("room1", "Player", "Connected", [new ChatParticipant("m2", "Jordan", true)],
        [
            new ChatMessage(1, "Jordan", "you on yet?", false, now.AddMinutes(-6), "Received"),
            new ChatMessage(2, "Jordan", "lobby is up on my side", false, now.AddMinutes(-5.5), "Received"),
            new ChatMessage(3, "Player", "yeah loading in now", true, now.AddMinutes(-5), "Delivered"),
            new ChatMessage(4, "Player", "do you see the LAN game?", true, now.AddMinutes(-4.8), "Delivered"),
            new ChatMessage(5, "Jordan", "yes, it shows up as Player's lobby. Picking the same map as last time so the first round loads fast for both of us.", false, now.AddMinutes(-1), "Received"),
            new ChatMessage(6, "Player", "joining", true, now, "Sending"),
        ]);
        var incoming = new List<ChatMessage>(); vm.IncomingChat += incoming.Add;
        var setChat = typeof(AppViewModel).GetMethod("SetChat", BindingFlags.Instance | BindingFlags.NonPublic)!;
        setChat.Invoke(vm, [history]);
        Check(incoming.Count == 0, "Existing messages are history, not notifications");
        var reply = history with { Messages = [.. history.Messages, new ChatMessage(7, "Jordan", "in", false, now, "Received")] };
        setChat.Invoke(vm, [reply]); setChat.Invoke(vm, [reply]);
        Check(incoming.Count == 1 && incoming[0].Id == 7, "Each friend message notifies once");
        Set(vm, "Chat", history); Notify(vm, "Chat");
        ClickNamed(window, "ChatNav");
        Render(window, output, "chat", 1180, 760);
        Render(window, output, "chat-small", 900, 620);
        Check(Descendants<TextBlock>(window).Count(t => t.Text.EndsWith("Sending…")) == 1 && !Descendants<TextBlock>(window).Any(t => t.Text.Contains("Delivered")), "Delivery shown only while pending");
        Check(Descendants<TextBlock>(window).Count(t => t.Text == "Jordan") == 2, "Friend name once per turn");
        Check(!Contains(window, "Join a room to chat") && !Contains(window, "Room only"), "No redundant chat captions");
        ClickNamed(window, "RoomNav");
        var diagram = (NetworkDiagram)window.FindName("Diagram");
        Check(!diagram.State.LanReady && diagram.GlowLayerCount == 0, "PC tunnel alone cannot illuminate console readiness");
        Render(window, output, "ready-local", 1180, 720, new(true, false, false, true));
        Check(diagram.GlowLayerCount == 1, "Only local half glows when local side is ready");
        Render(window, output, "ready-remote", 1180, 720, new(false, true, false, false, true));
        Check(diagram.GlowLayerCount == 1, "Remote-only readiness is supported independently");
        Render(window, output, "ready-sides-no-tunnel", 1180, 720, new(true, true, false, true, true));
        Check(!diagram.State.LanReady && diagram.GlowLayerCount == 2, "Both sides without tunnel keep center unlit");
        var allReady = new NetworkVisualState(true, true, true, true, true);
        Render(window, output, "ready-all-small", 900, 620, allReady);
        Render(window, output, "ready-all-max", 1280, 720, allReady);
        Render(window, output, "ready-all", 1180, 720, allReady);
        Render(window, output, "showcase-ethernet-ready", 1180, 720, allReady, showcase: true);
        Set(vm, "WifiNetwork", new IpcWifiNetwork("CODCONNECT-7F2A", "k7mq4xp2", "2.4 GHz")); Notify(vm, "WifiNetwork");
        Render(window, output, "showcase-wifi-ready", 1180, 720, allReady, showcase: true);

        List<IpcPlayer> squad =
        [
            new("Kyle", true, "PS5", true, false),
            new("Jackson", true, "PS4", true, true),
            new("Ana", true, null, false, false),
            new("Mo", false, null, false, false),
            new("Christopher-Longname", true, "PS4", true, false),
        ];
        foreach (var count in new[] { 1, 2, 5 })
        {
            var players = squad.Take(count).ToList();
            Set(vm, "Players", players); Notify(vm, "Players");
            var visual = new NetworkVisualState(true, players.All(p => p.ConsoleReady), true, true, true,
                players.Select(p => new FriendVisual(p.Name, p.Connected, p.ConsoleReady, RoomBadge.PlayerDetail(p))).ToArray());
            Render(window, output, $"players-{count + 1}", 1180, 720, visual, showcase: true);
            Render(window, output, $"players-{count + 1}-small", 900, 620, visual, showcase: true);
            Check(count == 1 ? Contains(window, "Kyle") && Contains(window, "PS5") : Contains(window, "Friends"), $"{count + 1} players: friend row");
        }

        Check(Contains(window, "Kyle and 3 others joined"), "Badge names who joined");
        Check(Contains(window, "3 of 5 consoles ready"), "Several friends share one summary row");
        Check(RoomBadge.PlayerDetail(squad[1]) == "PS4, via relay" && RoomBadge.PlayerDetail(squad[3]) == "Connecting" && RoomBadge.PlayerDetail(squad[2]) == "No console yet", "Each player's detail says where they are");
        Check(diagram.GlowLayerCount == 4 && !diagram.State.LanReady, "One lit spoke per ready player (you and three friends); the middle waits for every console");
        Check(RoomBadge.Describe(Screen.Connected, [new("Kyle", true, "PS5", true, false)]) == "Kyle joined", "One friend by name");
        Check(RoomBadge.Describe(Screen.Hosting, [new("Kyle", false, null, false, false)]) == "Connecting to Kyle", "Friend on the way");
        Check(RoomBadge.Change(["Kyle"], ["Kyle", "Ana"]) == "Ana joined" && RoomBadge.Change(["Kyle", "Ana"], ["Kyle"]) == "Ana left", "Arrivals and departures");
        Set(vm, "Players", new List<IpcPlayer>()); Notify(vm, "Players");
        Render(window, output, "wifi-ready-small", 900, 620, allReady);
        Check(Contains(window, "CODCONNECT-7F2A") && Contains(window, "On your console, join"), "Wi-Fi room shows the network to join");
        Set(vm, "WifiNetwork", null!); Notify(vm, "WifiNetwork");
        Render(window, output, "ready-all", 1180, 720, allReady);
        Check(diagram.State.LanReady && diagram.GlowLayerCount == 3, "Both ready sides and live tunnel illuminate center");
        Check(!diagram.HasAnimatedGlow, "Reduced motion preserves static glow without clocks");
        diagram.ReduceMotion = false; Pump(100);
        var motionAllowed = SystemParameters.ClientAreaAnimation && !SystemParameters.HighContrast;
        Check(diagram.HasAnimatedGlow == motionAllowed, "Motion follows Windows animation preference");
        var opacityBefore = diagram.GlowOpacity; Pump(450);
        Check(!motionAllowed || Math.Abs(diagram.GlowOpacity - opacityBefore) > .001, "Glow animation advances without state polling");
        diagram.Visibility = Visibility.Hidden;
        Check(!diagram.HasAnimatedGlow, "Hidden diagram releases animation clocks");
        diagram.Visibility = Visibility.Visible;
        window.WindowState = WindowState.Minimized;
        Check(!diagram.HasAnimatedGlow, "Minimized window releases animation clocks");
        window.WindowState = WindowState.Normal; window.UpdateLayout();
        Check(diagram.HasAnimatedGlow == motionAllowed, "Glow resumes after restore if motion permitted");
        diagram.ReduceMotion = true;
        Render(window, output, "ready-peer-lost", 1180, 720, new(true, false, false, true));
        Check(!diagram.State.LanReady && diagram.GlowLayerCount == 1, "Lost peer clears opposite half and center");
        diagram.State = NetworkVisualState.FromScreen(Screen.Home); window.UpdateLayout();
        Check(diagram.GlowLayerCount == 0 && !diagram.HasAnimatedGlow, "Leaving clears readiness glow and clocks");
        Console.WriteLine($"Windows glow animation enabled: {motionAllowed}");
        ClickNamed(window, "AdaptersNav");
        Check(Descendants<RadioButton>(window).Where(r => r.GroupName == "Adapters").All(r => !r.IsEnabled), "Adapters locked during session");
        Set(vm, "IsBusy", true); Notify(vm, "IsBusy");
        Check(Descendants<Button>(window).Single(b => Equals(b.Content, "Leave room")).IsEnabled == false, "Busy action guarded");
        Set(vm, "IsBusy", false); Set(vm, "Screen", Screen.Home); ClickNamed(window, "RoomNav");
        Render(window, output, "home-small", 900, 620);
        window.NpcapMissing = true; window.SoftEtherClientMissing = true; typeof(MainWindow).GetMethod("RefreshScreen", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Render(window, output, "home-npcap-missing-small", 900, 620);
        Check(Contains(window, "CODCONNECT needs Npcap to reach your console. Install it, then come back."), "Missing Npcap is explained with a way to get it");
        Check(Contains(window, "CODCONNECT needs the SoftEther VPN Client to connect rooms."), "Missing SoftEther VPN Client is explained with a way to install it");
        window.NpcapMissing = false; window.SoftEtherClientMissing = false;
        vm.Adapters.Clear(); ClickNamed(window, "AdaptersNav");
        Render(window, output, "adapters-empty-small", 900, 620);
        window.Close(); app.Shutdown();
        Console.WriteLine($"PASS: {_checks} UI checks. Native WPF renders: {output}");
    }
    private static void Set(AppViewModel vm, string name, object value) => typeof(AppViewModel).GetProperty(name)!.SetValue(vm, value);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(nint hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern nint SendMessage(nint hwnd, int message, nint wParam, nint lParam);
    private static void Notify(AppViewModel vm, string name) => typeof(AppViewModel).GetMethod("OnPropertyChanged", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, [name]);
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); _checks++; }
    private static void Pump(int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(milliseconds) };
        timer.Tick += (_, _) => { timer.Stop(); frame.Continue = false; };
        timer.Start(); Dispatcher.PushFrame(frame);
    }
    private static bool Contains(Window window, string text) { window.UpdateLayout(); return Descendants<TextBlock>(window).Any(t => t.Text == text); }
    private static void Click(Window window, string content) => Descendants<Button>(window).First(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void ClickNamed(Window window, string name) { ((Button)window.FindName(name)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); window.UpdateLayout(); }
    private static IEnumerable<T> Descendants<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        { var child = VisualTreeHelper.GetChild(parent, i); if (child is T match) yield return match; foreach (var item in Descendants<T>(child)) yield return item; }
    }
    private static void Render(Window window, string output, string name, int width, int height, NetworkVisualState? state = null, bool showcase = false)
    {
        window.Width = width; window.Height = height;
        window.UpdateLayout();
        if (state is not null)
        {
            var diagram = (NetworkDiagram)window.FindName("Diagram");
            diagram.ReduceMotion = true; diagram.State = state;
            ((TextBlock)window.FindName("CanvasHint")).Text = "Design fixture · " + (state.LanReady ? "both sides ready" : "independent side readiness");
            ((TextBlock)window.FindName("CanvasHint")).Visibility = showcase ? Visibility.Collapsed : Visibility.Visible;
            if (state.Friends is null)
                ((TextBlock)window.FindName("NetworkBadge")).Text = state.RemoteAttached ? "Friend joined" : "Waiting for friend";
            foreach (var row in Descendants<Grid>((ContentControl)window.FindName("ScreenHost")))
            {
                var text = row.Children.OfType<TextBlock>().ToArray();
                if (text.Length != 2) continue;
                if (text[0].Text == "Your console") text[1].Text = state.LocalReady ? "PlayStation" : "Not identified";
                if (text[0].Text == "Friend’s console") text[1].Text = state.RemoteReady ? "PlayStation" : "Not identified";
                if (text[0].Text == "Connection") text[1].Text = state.TunnelConnected ? "Direct" : "Pending";
                if (text[0].Text == "Friend’s PC") text[1].Text = state.RemoteAttached ? "Connected" : "Waiting";
            }
            window.UpdateLayout();
        }
        Check(!Descendants<ScrollBar>(window).Any(s => s.IsVisible && s.ActualWidth > 0 && s.ActualHeight > 0), name + ": no visible scrollbars");
        var host = (ContentControl)window.FindName("ScreenHost");
        Check(((FrameworkElement)host.Content).DesiredSize.Height <= host.ActualHeight + 1, name + ": room panel fits");
        Check(Descendants<Button>(host).Where(b => b.IsVisible).All(b => b.TransformToAncestor(host).TransformBounds(new Rect(b.RenderSize)).Bottom <= host.ActualHeight + 1),
            name + ": every room action is on screen");
        var details = (FrameworkElement)window.FindName("DetailContent");
        var detailView = (FrameworkElement)window.FindName("DetailView");
        if (detailView.IsVisible) Check(details.DesiredSize.Height <= detailView.ActualHeight + 1, name + ": secondary view fits");
        var root = (FrameworkElement)window.Content;
        var bitmap = new RenderTargetBitmap((int)root.ActualWidth, (int)root.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) { dc.DrawRectangle((Brush)window.FindResource("BgBrush"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight)); }
        bitmap.Render(visual); bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(Path.Combine(output, name + ".png")); encoder.Save(stream);
    }
}
