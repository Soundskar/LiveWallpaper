using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using LibVLCSharp.Shared;

namespace LiveWallpaper;

/// <summary>
/// Fullscreen borderless window that renders the video/GIF and is parented
/// behind the desktop icons.
/// </summary>
public partial class MainWindow : Window
{
    private LibVLC? _libVLC;
    private MediaPlayer? _player;
    private readonly DispatcherTimer _clickThroughTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private Native.EnumChildProc? _markChild;   // kept alive against GC

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _libVLC = new LibVLC(enableDebugLogs: false);
        _player = new MediaPlayer(_libVLC)
        {
            EnableHardwareDecoding = true,
            Mute = true,
        };
        VideoView.MediaPlayer = _player;

        // Attach behind the desktop icons once we have a native handle.
        var hwnd = new WindowInteropHelper(this).Handle;
        WorkerW.AttachToDesktop(hwnd);

        // The wallpaper is a non-interactive backdrop. Make it fully click-through so
        // that — even if embedding into WorkerW doesn't take on this system — it can
        // never intercept desktop clicks (e.g. right-click for the context menu).
        long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW));
        ((HwndSource)PresentationSource.FromVisual(this)!).AddHook(WallpaperWndProc);

        // VLC hosts its own child HWNDs that would otherwise intercept desktop clicks
        // (and it recreates them when media changes), so keep re-marking the whole
        // window tree click-through every second.
        _markChild = (child, _) => { Native.AddTransparent(child); return true; };
        _clickThroughTimer.Tick += (_, _) => Native.MakeTreeClickThrough(hwnd, _markChild);
        _clickThroughTimer.Start();
        Native.MakeTreeClickThrough(hwnd, _markChild);
    }

    // Everything on the wallpaper reports "transparent" to the mouse, so all clicks
    // fall through to the desktop underneath.
    private static IntPtr WallpaperWndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_NCHITTEST) { handled = true; return Native.HTTRANSPARENT; }
        return IntPtr.Zero;
    }

    /// <summary>Loads and loops a video/GIF file as the wallpaper.</summary>
    public void PlayFile(string path)
    {
        if (_libVLC == null || _player == null || !File.Exists(path))
            return;

        // input-repeat gives seamless looping for videos and GIFs alike.
        var media = new Media(_libVLC, new Uri(path));
        media.AddOption(":input-repeat=65535");
        media.AddOption(":no-audio");
        _player.Play(media);
        media.Dispose();
    }

    public void SetPaused(bool paused)
    {
        _player?.SetPause(paused);
    }

    public bool IsPlaying => _player?.IsPlaying ?? false;

    protected override void OnClosed(EventArgs e)
    {
        _player?.Stop();
        _player?.Dispose();
        _libVLC?.Dispose();
        base.OnClosed(e);
    }
}
