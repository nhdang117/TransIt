using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using TransIt.Core;
using TransIt.Infrastructure;

namespace TransIt.Windows.Chat;

public partial class ChatWindow : Window
{
    private AssistantsChatService? _service;
    private bool _isSending;
    private CancellationTokenSource _cts = new();

    private IntPtr _kbHook = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _kbProcDelegate;

    private Border? _loadingBubble;

    public ChatWindow()
    {
        InitializeComponent();
    }

    // Opens window in loading state while session is being started by SummaryMode.
    public void ShowLoading()
    {
        LoadingPanel.Visibility = Visibility.Visible;
        InputRow.IsEnabled = false;
        Show();
        Activate();
        InstallEscHook();
    }

    // Called by SummaryMode once session is ready — shows summary as first AI bubble.
    public void AddSummary(string summary, AssistantsChatService service)
    {
        _service = service;
        LoadingPanel.Visibility = Visibility.Collapsed;
        InputRow.IsEnabled = true;
        AddBubble(summary, isUser: false);
        ScrollToBottom();
        InputBox.Focus();
    }

    private void Header_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left) DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => CloseChat();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !_isSending)
        {
            e.Handled = true;
            _ = SendMessageAsync();
        }
    }

    private void SendBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!_isSending) _ = SendMessageAsync();
    }

    private async Task SendMessageAsync()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text) || _service is null) return;

        _isSending = true;
        SendBtn.IsEnabled = false;
        InputBox.IsEnabled = false;
        InputBox.Clear();

        AddBubble(text, isUser: true);
        ScrollToBottom();

        _loadingBubble = AddBubble("…", isUser: false);
        ScrollToBottom();

        string response;
        try
        {
            response = await _service.SendMessageAsync(text, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            response = $"[Lỗi: {ex.Message}]";
        }

        if (_loadingBubble is not null)
        {
            ChatPanel.Children.Remove(_loadingBubble);
            _loadingBubble = null;
        }

        AddBubble(response, isUser: false);
        ScrollToBottom();

        _isSending = false;
        SendBtn.IsEnabled = true;
        InputBox.IsEnabled = true;
        InputBox.Focus();
    }

    private Border AddBubble(string text, bool isUser)
    {
        var tb = new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 13,
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
            Margin = new Thickness(10, 7, 10, 7)
        };

        var bubble = new Border
        {
            Background = new SolidColorBrush(isUser
                ? Color.FromRgb(0x25, 0x63, 0xEB)
                : Color.FromRgb(0x2A, 0x2A, 0x2A)),
            CornerRadius = new CornerRadius(8),
            MaxWidth = ChatPanel.ActualWidth > 0 ? ChatPanel.ActualWidth * 0.85 : 380,
            HorizontalAlignment = isUser ? HorizontalAlignment.Right : HorizontalAlignment.Left,
            Margin = new Thickness(0, 3, 0, 3),
            Child = tb
        };

        ChatPanel.Children.Add(bubble);
        return bubble;
    }

    private void ScrollToBottom()
    {
        ChatScroll.UpdateLayout();
        ChatScroll.ScrollToEnd();
    }

    private void CloseChat()
    {
        UninstallEscHook();
        Close();
    }

    private void InstallEscHook()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProcDelegate = EscHookProc;
        _kbHook = NativeMethods.SetWindowsHookEx(
            NativeMethods.WH_KEYBOARD_LL,
            _kbProcDelegate,
            NativeMethods.GetModuleHandle(null),
            0);
    }

    private void UninstallEscHook()
    {
        if (_kbHook == IntPtr.Zero) return;
        NativeMethods.UnhookWindowsHookEx(_kbHook);
        _kbHook = IntPtr.Zero;
        _kbProcDelegate = null;
    }

    private IntPtr EscHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && wParam.ToInt32() == NativeMethods.WM_KEYDOWN)
        {
            var ks = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            if (ks.vkCode == NativeMethods.VK_ESCAPE)
            {
                Dispatcher.BeginInvoke(CloseChat);
                return new IntPtr(1);
            }
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        UninstallEscHook();
        // Fire-and-forget: delete thread, assistant, files from OpenAI
        if (_service is not null) _ = _service.DisposeAsync().AsTask();
        base.OnClosed(e);
    }
}
