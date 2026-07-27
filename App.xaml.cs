using System.Windows;
using LibVLCSharp.Shared;
using Forms = System.Windows.Forms;

namespace LiveWallpaper;

public partial class App : System.Windows.Application
{
    private Forms.NotifyIcon? _tray;
    private MainWindow? _wallpaper;
    private Settings _settings = new();
    private bool _paused;

    private Forms.ToolStripMenuItem? _pauseItem;
    private Forms.ToolStripMenuItem? _startupItem;
    private Forms.ToolStripMenuItem? _petItem;
    private PetWindow? _pet;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Required once before any LibVLC usage; loads the native VLC binaries.
        Core.Initialize();

        _settings = Settings.Load();

        _wallpaper = new MainWindow();
        _wallpaper.Show();

        BuildTray();

        if (_settings.PetEnabled)
            ShowPet();

        if (!string.IsNullOrEmpty(_settings.LastFile) && System.IO.File.Exists(_settings.LastFile))
            _wallpaper.PlayFile(_settings.LastFile);
        else
            ChooseFile();
    }

    private void TogglePet(bool show)
    {
        _settings.PetEnabled = show;
        _settings.Save();
        if (show) ShowPet();
        else HidePet();
    }

    private void ShowPet()
    {
        if (_pet != null) { return; }
        _pet = new PetWindow(_settings, () => _settings.Save());
        _pet.HiddenByUser += () => { if (_petItem != null) _petItem.Checked = false; TogglePet(false); };
        _pet.Closed += (_, _) => _pet = null;
        _pet.Show();
    }

    private void HidePet()
    {
        _pet?.StopPet();
        _pet = null;
    }

    private void BuildTray()
    {
        var menu = new Forms.ContextMenuStrip();

        var choose = new Forms.ToolStripMenuItem("Choose wallpaper…");
        choose.Click += (_, _) => ChooseFile();
        menu.Items.Add(choose);

        _pauseItem = new Forms.ToolStripMenuItem("Pause");
        _pauseItem.Click += (_, _) => TogglePause();
        menu.Items.Add(_pauseItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        _petItem = new Forms.ToolStripMenuItem("Show desktop pet 🐾")
        {
            Checked = _settings.PetEnabled,
            CheckOnClick = true,
        };
        _petItem.Click += (_, _) => TogglePet(_petItem.Checked);
        menu.Items.Add(_petItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        _startupItem = new Forms.ToolStripMenuItem("Start with Windows")
        {
            Checked = Settings.IsStartupEnabled(),
            CheckOnClick = true,
        };
        _startupItem.Click += (_, _) => Settings.SetStartup(_startupItem.Checked);
        menu.Items.Add(_startupItem);

        menu.Items.Add(new Forms.ToolStripSeparator());

        var exit = new Forms.ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitApp();
        menu.Items.Add(exit);

        _tray = new Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Application,
            Text = "Live Wallpaper",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ChooseFile();
    }

    private void ChooseFile()
    {
        var dlg = new Forms.OpenFileDialog
        {
            Title = "Choose a video or GIF for your wallpaper",
            Filter = "Media files|*.mp4;*.webm;*.gif;*.mkv;*.avi;*.mov;*.wmv|All files|*.*",
        };
        if (dlg.ShowDialog() == Forms.DialogResult.OK)
        {
            _settings.LastFile = dlg.FileName;
            _settings.Save();
            _wallpaper?.PlayFile(dlg.FileName);
            _paused = false;
            if (_pauseItem != null) _pauseItem.Text = "Pause";
        }
    }

    private void TogglePause()
    {
        _paused = !_paused;
        _wallpaper?.SetPaused(_paused);
        if (_pauseItem != null) _pauseItem.Text = _paused ? "Play" : "Pause";
    }

    private void ExitApp()
    {
        if (_tray != null) _tray.Visible = false;
        _tray?.Dispose();
        _pet?.StopPet();
        _wallpaper?.Close();
        Shutdown();
    }
}
