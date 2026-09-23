using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;

namespace CODConnect.UI;

public partial class MainWindow
{
    private static readonly TimeSpan ChatGroupWindow = TimeSpan.FromMinutes(3);

    private TextBox? _chatInput;
    private TextBlock? _chatFeedback;
    private ScrollViewer? _chatScroll;
    private StackPanel? _chatMessages;
    private Button? _chatSend;
    private string _chatDraft = "", _chatRoom = "", _chatRendered = "";
    private int _unreadChat;

    private void ShowChat()
    {
        ClearUnreadChat();
        var layout = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        layout.SetBinding(HeightProperty, new Binding(nameof(ActualHeight)) { Source = DetailView });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _chatMessages = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom };
        _chatScroll = new ScrollViewer
        {
            Content = _chatMessages,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Margin = new Thickness(0, 0, 0, 16),
            OpacityMask = new LinearGradientBrush(new GradientStopCollection
            {
                new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Black, 0.08), new GradientStop(Colors.Black, 1),
            }, 90),
        };
        _chatScroll.SizeChanged += (_, _) => { _chatRendered = ""; RenderChatMessages(); };
        layout.Children.Add(_chatScroll);

        var composer = new Grid(); composer.ColumnDefinitions.Add(new ColumnDefinition()); composer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _chatInput = new TextBox { Text = _chatDraft, MaxLength = 320, Height = 42, VerticalContentAlignment = VerticalAlignment.Center, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden };
        AutomationProperties.SetName(_chatInput, "Message to room");
        _chatInput.TextChanged += (_, _) => { _chatDraft = _chatInput.Text; UpdateChatComposer(); };
        _chatInput.KeyDown += async (_, e) => { if (e.Key == Key.Enter) { e.Handled = true; await SendChat(); } };
        _chatSend = Button("Send", async () => await SendChat(), true); _chatSend.Margin = new Thickness(8, 0, 0, 0); Grid.SetColumn(_chatSend, 1);
        composer.Children.Add(_chatInput); composer.Children.Add(_chatSend); Grid.SetRow(composer, 1); layout.Children.Add(composer);

        _chatFeedback = Text("", true, 11); _chatFeedback.Margin = new Thickness(0, 8, 0, 0); _chatFeedback.MinHeight = 19;
        AutomationProperties.SetLiveSetting(_chatFeedback, AutomationLiveSetting.Polite);
        Grid.SetRow(_chatFeedback, 2); layout.Children.Add(_chatFeedback);

        DetailContent.Children.Add(layout);
        _chatRendered = "";
        RefreshChat();
    }

    private void RefreshChat()
    {
        var room = _vm.Chat?.RoomId ?? "";
        if (_chatRoom != room)
        {
            if (_chatRoom.Length > 0) ChatToasts.Clear();
            _chatRoom = room; _chatDraft = ""; _chatRendered = "";
            ClearUnreadChat();
            if (_chatInput is not null) _chatInput.Text = "";
        }

        if (_page != "Chat" || _chatScroll is null) return;
        UpdateChatComposer(); RenderChatMessages();
    }

    private void UpdateChatComposer()
    {
        if (_chatInput is null || _chatSend is null || _chatFeedback is null) return;
        var ready = _vm.Chat?.Peers.Any(p => p.Connected) == true;
        _chatInput.IsEnabled = ready && !_vm.ChatSending;
        _chatSend.IsEnabled = ready && !_vm.ChatSending && !string.IsNullOrWhiteSpace(_chatDraft);

        var unencrypted = ready && _vm.Chat?.State.Contains("not encrypted", StringComparison.Ordinal) == true;
        _chatFeedback.Text = !string.IsNullOrEmpty(_vm.ChatError) ? _vm.ChatError
            : unencrypted ? "Same-network link: messages aren’t encrypted." : "";
        _chatFeedback.Foreground = Color("WarnBrush");
    }

    private async Task SendChat()
    {
        if (_chatSend?.IsEnabled != true) return;
        if (await _vm.SendChatAsync(_chatDraft))
        {
            _chatDraft = ""; if (_chatInput is not null) { _chatInput.Text = ""; _chatInput.Focus(); }
            _chatScroll?.ScrollToEnd();
        }
        RefreshChat();
    }

    private void RenderChatMessages()
    {
        if (_page != "Chat" || _chatMessages is null || _chatScroll is null) return;
        var messages = _vm.Chat?.Messages ?? [];
        var connected = _vm.Chat?.Peers.Any(p => p.Connected) == true;

        var signature = $"{_vm.IsHome}|{connected}|{messages.Count}|{(messages.Count > 0 ? messages[^1].Id : 0)}|"
            + string.Join(",", messages.Where(m => m.Own).Select(m => m.Delivery[0]));
        if (signature == _chatRendered) return;
        _chatRendered = signature;

        var atBottom = _chatScroll.VerticalOffset >= _chatScroll.ScrollableHeight - 4;
        _chatMessages.Children.Clear();

        if (messages.Count == 0)
        {
            var empty = Text(_vm.IsHome ? "Chat opens when you’re in a room." : connected ? "Say hello." : "Chat opens when your friend joins.", true, 13);
            empty.HorizontalAlignment = HorizontalAlignment.Center; empty.Margin = new Thickness(0, 0, 0, 24);
            _chatMessages.Children.Add(empty);
            return;
        }

        var bubbleWidth = Math.Max(180, _chatScroll.ActualWidth * 0.72);
        for (var i = 0; i < messages.Count;)
        {
            var first = messages[i];
            var turn = new List<ChatMessage> { first };
            while (i + turn.Count < messages.Count
                   && messages[i + turn.Count] is { } next
                   && next.Own == first.Own && next.Author == first.Author
                   && next.Time - turn[^1].Time <= ChatGroupWindow)
            {
                turn.Add(next);
            }

            _chatMessages.Children.Add(Turn(turn, bubbleWidth));
            i += turn.Count;
        }

        if (atBottom) _chatScroll.ScrollToEnd();
    }

    private StackPanel Turn(List<ChatMessage> turn, double bubbleWidth)
    {
        var own = turn[0].Own;
        var side = own ? HorizontalAlignment.Right : HorizontalAlignment.Left;
        var panel = new StackPanel { Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = side };

        if (!own)
        {
            var author = Text(turn[0].Author, true, 11);
            author.TextTrimming = TextTrimming.CharacterEllipsis; author.TextWrapping = TextWrapping.NoWrap;
            author.Margin = new Thickness(4, 0, 0, 4);
            panel.Children.Add(author);
        }

        foreach (var message in turn)
        {
            var body = Text(message.Text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' '), size: 13);
            body.TextWrapping = TextWrapping.Wrap;
            var bubble = new Border
            {
                Child = body,
                Background = Color(own ? "AccentSoftBrush" : "PanelLightBrush"),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(12, 8, 12, 8),
                Margin = new Thickness(0, 0, 0, 3),
                MaxWidth = bubbleWidth,
                HorizontalAlignment = side,
            };
            AutomationProperties.SetName(bubble, $"{(own ? "You" : message.Author)}: {message.Text}");
            panel.Children.Add(bubble);
        }

        var last = turn[^1];
        var pending = turn.FirstOrDefault(m => m.Own && m.Delivery != "Delivered");
        var meta = Text(last.Time.ToLocalTime().ToString("HH:mm") + (pending is null ? "" : pending.Delivery == "Sending" ? "  Sending…" : "  Not confirmed"), true, 10);
        if (pending is not null && pending.Delivery != "Sending") meta.Foreground = Color("WarnBrush");
        meta.HorizontalAlignment = side; meta.Margin = new Thickness(4, 1, 4, 0);
        panel.Children.Add(meta);
        return panel;
    }

    private void OnIncomingChat(ChatMessage message)
    {
        var windowVisible = IsActive && WindowState != WindowState.Minimized;
        if (windowVisible && _page == "Chat") return;

        _unreadChat++;
        ChatUnreadDot.Visibility = Visibility.Visible;
        ChatNav.ToolTip = _unreadChat == 1 ? "Room chat - 1 new message" : $"Room chat - {_unreadChat} new messages";

        if (!windowVisible)
        {
            ChatToasts.Show(message.Author, message.Text);
        }
    }

    private void ClearUnreadChat()
    {
        _unreadChat = 0;
        ChatUnreadDot.Visibility = Visibility.Collapsed;
        ChatNav.ToolTip = "Room chat";
    }

    private void OpenChatFromNotification()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show(); Activate();
        Navigate("Chat");
    }
}
