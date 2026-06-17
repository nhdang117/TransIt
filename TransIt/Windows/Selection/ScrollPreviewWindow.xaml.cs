using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using TransIt.Infrastructure;

namespace TransIt.Windows.Selection;

public partial class ScrollPreviewWindow : Window
{
    private Bitmap? _composite;
    private int _frameCount;
    private bool _closed;

    // Thumbnail width in physical pixels. Frames are scaled to this width before stitching.
    private const int ThumbWidth = 170;

    public ScrollPreviewWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) DragMove(); };
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        NativeMethods.SetWindowDisplayAffinity(hwnd, NativeMethods.WDA_EXCLUDEFROMCAPTURE);

        var screen = SystemParameters.WorkArea;
        Left = screen.Right - Width - 16;
        // Position above ScrollCaptureBar (72px tall + 16px margin + 8px gap)
        Top  = Math.Max(8, screen.Bottom - 72 - 16 - 8 - Height);
    }

    // Called from capture thread. Stitches frame into the growing composite and refreshes UI.
    public void AddFrame(byte[] jpegBytes)
    {
        if (_closed) return;

        using var ms = new MemoryStream(jpegBytes);
        using var frame = new Bitmap(ms);

        int thumbH = Math.Max(1, (int)((double)frame.Height * ThumbWidth / frame.Width));
        using var thumb = new Bitmap(frame, new System.Drawing.Size(ThumbWidth, thumbH));

        int prevH = _composite?.Height ?? 0;
        var merged = new Bitmap(ThumbWidth, prevH + thumbH);
        using (var g = Graphics.FromImage(merged))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            if (_composite != null)
                g.DrawImage(_composite, 0, 0);
            g.DrawImage(thumb, 0, prevH);
        }
        _composite?.Dispose();
        _composite = merged;
        _frameCount++;

        // Convert to WPF BitmapSource via LockBits (no encode/decode round-trip).
        // Freeze() makes it safe to hand across threads.
        var src = ToFrozenBitmapSource(merged);

        int count = _frameCount;
        Dispatcher.Invoke(() =>
        {
            if (_closed) return;
            PreviewImage.Source = src;
            CountLabel.Text = $"{count} image{(count == 1 ? "" : "s")}";
            Scroller.UpdateLayout();
            Scroller.ScrollToBottom();
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _closed = true;
        _composite?.Dispose();
        _composite = null;
        base.OnClosed(e);
    }

    private static BitmapSource ToFrozenBitmapSource(Bitmap bmp)
    {
        var rect = new System.Drawing.Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var src = BitmapSource.Create(
                bmp.Width, bmp.Height,
                bmp.HorizontalResolution, bmp.VerticalResolution,
                PixelFormats.Bgra32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            src.Freeze();
            return src;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }
}
