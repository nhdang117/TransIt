using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using TransIt.Infrastructure;

namespace TransIt.Windows.TextPane;

public partial class TextPaneWindow : Window
{
    private IntPtr _kbHook = IntPtr.Zero;
    private NativeMethods.LowLevelKeyboardProc? _kbProcDelegate;

    public TextPaneWindow()
    {
        InitializeComponent();
    }

    public void ShowLoading()
    {
        LoadingText.Text = "Text OCR…";
        LoadingPanel.Visibility = Visibility.Visible;
        ResultsPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Children.Clear();
        Show();
        Activate();
        InstallEscHook();
    }

    public void UpdateLoadingStatus(string text) => LoadingText.Text = text;

    public void ShowTranslation(IList<string> blocks)
    {
        ResultsPanel.Children.Clear();

        if (blocks.Count == 0)
        {
            AddResultBlock("(No text detected)");
        }
        else
        {
            for (int i = 0; i < blocks.Count; i++)
            {
                AddResultBlock(blocks[i]);
                if (i < blocks.Count - 1)
                    ResultsPanel.Children.Add(new Rectangle
                    {
                        Height = 1,
                        Fill = new SolidColorBrush(Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF)),
                        Margin = new Thickness(0, 8, 0, 8)
                    });
            }
        }

        LoadingPanel.Visibility = Visibility.Collapsed;
        ResultsPanel.Visibility = Visibility.Visible;
    }

    private void AddResultBlock(string text)
    {
        ResultsPanel.Children.Add(new TextBlock
        {
            Text = text,
            Foreground = Brushes.White,
            FontSize = 14,
            FontFamily = new FontFamily("Segoe UI"),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 22
        });
    }

    private void Header_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton == System.Windows.Input.MouseButton.Left)
            DragMove();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => ClosePane();

    private void ClosePane()
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
                Dispatcher.BeginInvoke(ClosePane);
                return new IntPtr(1);
            }
        }
        return NativeMethods.CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    protected override void OnClosed(EventArgs e)
    {
        UninstallEscHook();
        base.OnClosed(e);
    }
}
