using System.IO;
using System.Windows;
using System.Windows.Interop;
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
