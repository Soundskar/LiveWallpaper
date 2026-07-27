using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LiveWallpaper;

/// <summary>
/// A lightweight vector "desktop pet" that lives strictly on the desktop layer
/// (never over other apps), wanders and reacts to the mouse, and provides small
/// useful utilities (reminders, focus timer, clock) plus care mechanics.
/// </summary>
public partial class PetWindow : Window
{
    private enum State { Idle, Walk, Sit, Sleep, Jump, Fall, Drag, Chase, Focus }

    private readonly DispatcherTimer _timer = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rng = new();
    private readonly Settings _settings;
    private readonly Action _save;

    private State _state = State.Idle;
    private double _lastSeconds;
    private double _vx, _vy;
    private double _nextDecision, _nextBlink;
    private double _lastSaveAt, _lastSlowChecks;

    // interaction (DPI-correct dragging)
    private bool _dragging;
    private double _dpiX = 1, _dpiY = 1;
    private Point _dragStartCursor;
    private double _dragStartLeft, _dragStartTop;
    private readonly List<double> _recentClicks = new();

    // reusable visual objects (avoid per-frame allocation)
    private readonly TranslateTransform _pupil = new();
    private readonly ScaleTransform _leftBlink = new(1, 1);
    private readonly ScaleTransform _rightBlink = new(1, 1);
    private readonly SolidColorBrush _bodyBrush = new(Color.FromRgb(0x4F, 0xD1, 0xC5));

    // stats
    private double _happiness, _hunger, _energy;
    private bool _party;
    private double _partyHue;
    private double _bubbleHideAt = -1;
    private double _chaseUntil, _focusUntil;
    private bool _suspended;   // fullscreen app in foreground
    private bool _clickThrough = true;

    // reminder / clock accumulators (seconds since last fire)
    private double _eyeTimer, _waterTimer, _stretchTimer;
    private const double EyeInterval = 20 * 60;      // 20-20-20 rule
    private const double WaterInterval = 60 * 60;    // hourly hydration
    private const double StretchInterval = 90 * 60;  // posture / stretch

    private string _name;
    public event Action? HiddenByUser;

    private readonly string[] _quips =
    {
        "Hi there!", "What'cha doing?", "Boop!", "Looking good!",
        "You've got this!", "Let's play!", "Nice desktop!", "*happy wiggle*",
    };

    public PetWindow(Settings settings, Action save)
    {
        InitializeComponent();
        _settings = settings;
        _save = save;
        _name = string.IsNullOrWhiteSpace(settings.PetName) ? "Pixel" : settings.PetName;

        _happiness = settings.PetHappiness;
        _hunger = settings.PetHunger;
        _energy = settings.PetEnergy;
        ApplyAwayDecay();

        Body.Fill = _bodyBrush;
        LeftPupil.RenderTransform = _pupil;
        RightPupil.RenderTransform = _pupil;
        LeftEye.RenderTransformOrigin = new Point(0.5, 0.5);
        RightEye.RenderTransformOrigin = new Point(0.5, 0.5);
        LeftEye.RenderTransform = _leftBlink;
        RightEye.RenderTransform = _rightBlink;

        Loaded += OnLoaded;
        PetBox.MouseLeftButtonDown += OnPetMouseDown;
        PetBox.MouseLeftButtonUp += OnPetMouseUp;
        PetBox.MouseRightButtonUp += (_, _) => ShowMenu();
        MouseMove += OnMouseMove;
    }

    // ---- decay while the app was closed, based on real elapsed time ----
    private void ApplyAwayDecay()
    {
        double hours = Math.Max(0, (DateTime.UtcNow - _settings.PetLastSeenUtc).TotalHours);
        hours = Math.Min(hours, 48); // cap so it never "dies" from a long absence
        _hunger = Clamp(_hunger + hours * 5);
        _happiness = Clamp(_happiness - hours * 3);
        _energy = Clamp(_energy + hours * 12); // rests while away
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX; _dpiY = dpi.DpiScaleY;

        var hwnd = new WindowInteropHelper(this).Handle;
        // Non-activating tool window so it never steals focus or shows in Alt-Tab.
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_NOACTIVATE | Native.WS_EX_TOOLWINDOW);
        SetClickThrough(true);
        SendToDesktopLayer();
    }

    private void SendToDesktopLayer()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        Native.SetWindowPos(hwnd, Native.HWND_BOTTOM, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        ApplyGrowth();
        SnapToGround();
        Left = Math.Max(WorkArea.Left, Math.Min(Left, WorkArea.Right - Width));
        _nextDecision = Now + 1;
        _nextBlink = Now + 2;
        SendToDesktopLayer();
        GreetByTime();

        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += Tick;
        _timer.Start();
    }

    private double Now => _clock.Elapsed.TotalSeconds;
    private static Rect WorkArea => SystemParameters.WorkArea;
    private double Ground => WorkArea.Bottom - Height;
    private void SnapToGround() => Top = Ground;
    private static double Clamp(double v) => Math.Max(0, Math.Min(100, v));

    // grow from baby to adult over the first week
    private void ApplyGrowth()
    {
        double days = (DateTime.UtcNow - _settings.PetBornUtc).TotalDays;
        double s = 0.8 + Math.Min(0.2, days * 0.03);
        PetBox.LayoutTransform = new ScaleTransform(s, s);
    }

    // ------------------------------------------------------------- main loop
    private void Tick(object? sender, EventArgs e)
    {
        double t = Now;
        double dt = Math.Min(0.05, t - _lastSeconds);
        _lastSeconds = t;

        // Low-frequency housekeeping (~1s): fullscreen suspend, battery, save.
        if (t - _lastSlowChecks >= 1.0)
        {
            _lastSlowChecks = t;
            UpdateSuspendState();
            if (!_suspended) { SendToDesktopLayer(); UpdateReminders(); UpdateBattery(); }
            if (t - _lastSaveAt >= 30) { PersistState(); _lastSaveAt = t; }
        }

        if (_suspended) return;   // do nothing while a fullscreen app is up

        bool hovered = UpdateClickThrough();

        Blink(t);
        if (hovered || _state == State.Chase) FollowCursorWithEyes();
        if (_party) StepParty();

        switch (_state)
        {
            case State.Idle: case State.Sit: case State.Sleep: case State.Focus: StepIdle(t, dt); break;
            case State.Walk: StepWalk(dt); break;
            case State.Chase: StepChase(t, dt); break;
            case State.Jump: case State.Fall: StepAirborne(dt); break;
        }

        if (_bubbleHideAt > 0 && t >= _bubbleHideAt) { Bubble.Visibility = Visibility.Collapsed; _bubbleHideAt = -1; }

        if (_state is State.Idle or State.Walk)
            Bob.Y = Math.Sin(t * (_state == State.Walk ? 12 : 2.2)) * (_state == State.Walk ? 3 : 1.5);
        else if (_state is not State.Jump and not State.Fall and not State.Drag)
            Bob.Y = 0;

        // adaptive frame rate: fast only when something is actually moving
        bool active = _dragging || _state is State.Walk or State.Jump or State.Fall or State.Chase || hovered || _party;
        var target = TimeSpan.FromMilliseconds(active ? 33 : 160);
        if (_timer.Interval != target) _timer.Interval = target;
    }

    private void StepIdle(double t, double dt)
    {
        // slow natural drift of stats
        _hunger = Clamp(_hunger + dt * 0.03);
        _happiness = Clamp(_happiness - dt * 0.01);
        if (_state != State.Focus && t >= _focusUntil && t >= _nextDecision) Decide();
        if (_state == State.Focus && t >= _focusUntil) EndFocus();
    }

    private void StepWalk(double dt)
    {
        Left += _vx * dt;
        Facing.ScaleX = _vx < 0 ? -1 : 1;
        if (Left <= WorkArea.Left) { Left = WorkArea.Left; _vx = Math.Abs(_vx); }
        if (Left >= WorkArea.Right - Width) { Left = WorkArea.Right - Width; _vx = -Math.Abs(_vx); }
        if (Now >= _nextDecision) Decide();
    }

    private void StepChase(double t, double dt)
    {
        double targetX = CursorLocal().X + Left - Width / 2;
        double dx = targetX - Left;
        _vx = Math.Sign(dx) * 220;
        if (Math.Abs(dx) > 4) Left += _vx * dt;
        Facing.ScaleX = dx < 0 ? -1 : 1;
        Bob.Y = Math.Sin(t * 14) * 3;
        if (t >= _chaseUntil) { _vx = 0; _state = State.Idle; ScheduleDecision(1, 3); }
    }

    private void StepAirborne(double dt)
    {
        _vy += 1600 * dt;
        Top += _vy * dt;
        Left = Math.Clamp(Left + _vx * dt, WorkArea.Left, WorkArea.Right - Width);
        if (Top >= Ground)
        {
            Top = Ground; _vy = 0; _vx = 0;
            Squish();
            _state = State.Idle;
            ScheduleDecision(1, 3);
        }
    }

    // ------------------------------------------------------------- behavior
    private void Decide()
    {
        int hour = DateTime.Now.Hour;
        bool night = hour >= 23 || hour < 6;
        int roll = _rng.Next(100);

        if (night && roll < 70) { _state = State.Sleep; _vx = 0; SetMouth(true); ScheduleDecision(6, 12); return; }

        if (roll < 33) { _state = State.Walk; _vx = (_rng.Next(2) == 0 ? -1 : 1) * _rng.Next(45, 85); ScheduleDecision(2, 5); }
        else if (roll < 58) { _state = State.Idle; _vx = 0; ScheduleDecision(2, 4); }
        else if (roll < 73) { _state = State.Sit; _vx = 0; ScheduleDecision(3, 6); }
        else if (roll < 83) { _state = State.Sleep; _vx = 0; SetMouth(true); ShowBubble("Zzz…", 2500); ScheduleDecision(4, 8); }
        else if (roll < 93) { Jump(); ScheduleDecision(1, 2); }
        else { if (_rng.Next(3) == 0) Say(); ScheduleDecision(2, 4); }

        if (_state != State.Sleep) SetMouth(false);
    }

    private void ScheduleDecision(double min, double max) => _nextDecision = Now + min + _rng.NextDouble() * (max - min);

    private void Jump()
    {
        if (_state is State.Jump or State.Fall or State.Drag) return;
        _state = State.Jump; _vy = -600; _vx = (_rng.Next(3) - 1) * 60;
    }

    private void Squish()
    {
        var sx = new DoubleAnimation(1.25, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        var sy = new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        Squash.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        Squash.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    // ------------------------------------------------------------- utilities
    private void GreetByTime()
    {
        int h = DateTime.Now.Hour;
        string part = h < 12 ? "morning" : h < 18 ? "afternoon" : "evening";
        ShowBubble($"Good {part}! I'm {_name} 🐾", 3000);
    }

    private void UpdateReminders()
    {
        if (!_settings.PetReminders || _state == State.Drag) return;
        _eyeTimer += 1; _waterTimer += 1; _stretchTimer += 1;
        if (_eyeTimer >= EyeInterval) { _eyeTimer = 0; ShowBubble("👀 Look 20ft away for 20s", 6000); Say(false); }
        else if (_waterTimer >= WaterInterval) { _waterTimer = 0; ShowBubble("💧 Time for some water!", 6000); }
        else if (_stretchTimer >= StretchInterval) { _stretchTimer = 0; ShowBubble("🧘 Stand up & stretch!", 6000); }
    }

    private void UpdateBattery()
    {
        var p = Forms.SystemInformation.PowerStatus;
        if (p.PowerLineStatus == Forms.PowerLineStatus.Offline && p.BatteryLifePercent <= 0.20f && _rng.Next(30) == 0)
            ShowBubble("🔋 Low battery…", 4000);
    }

    private void StartFocus(int minutes)
    {
        _state = State.Focus; _vx = 0; _focusUntil = Now + minutes * 60;
        SetMouth(false);
        ShowBubble($"Focus time! {minutes}m 📚", 3000);
    }

    private void EndFocus()
    {
        _state = State.Idle;
        _happiness = Clamp(_happiness + 10);
        ShowBubble("Done! Take a break 🎉", 5000);
        Jump();
        ScheduleDecision(1, 2);
    }

    // ------------------------------------------------------------- face
    private void Blink(double t)
    {
        if (t < _nextBlink) return;
        _nextBlink = t + 2 + _rng.NextDouble() * 4;
        var a = new DoubleAnimation(1, 0.1, TimeSpan.FromMilliseconds(80)) { AutoReverse = true };
        _leftBlink.BeginAnimation(ScaleTransform.ScaleYProperty, a);
        _rightBlink.BeginAnimation(ScaleTransform.ScaleYProperty, a.Clone());
    }

    private void FollowCursorWithEyes()
    {
        Point c = CursorLocal();
        double cx = Width / 2, cy = Height - 60;
        double ang = Math.Atan2(c.Y - cy, c.X - cx);
        double dist = Math.Min(2.5, Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)) / 30);
        _pupil.X = Math.Cos(ang) * dist;
        _pupil.Y = Math.Sin(ang) * dist;
    }

    private void SetMouth(bool sleepy) =>
        Mouth.Data = sleepy ? Geometry.Parse("M54,86 L66,86") : Geometry.Parse("M54,84 Q60,90 66,84");

    private void Smile()
    {
        Mouth.Data = Geometry.Parse("M52,82 Q60,94 68,82");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { SetMouth(false); t.Stop(); };
        t.Start();
    }

    // ------------------------------------------------------------- speech
    private void Say(bool allow = true) { if (allow) ShowBubble(_quips[_rng.Next(_quips.Length)], 2200); }

    private void ShowBubble(string text, int ms)
    {
        BubbleText.Text = text;
        Bubble.Visibility = Visibility.Visible;
        _bubbleHideAt = Now + ms / 1000.0;
    }

    // ------------------------------------------------------------- mouse
    private void OnPetMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Happy(); return; }
        RegisterClick();
        _dragging = true;
        _dragStartCursor = CursorScreenPx();
        _dragStartLeft = Left; _dragStartTop = Top;
        _state = State.Drag; Bob.Y = 0;
        PetBox.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point m = CursorScreenPx();
        double dx = (m.X - _dragStartCursor.X) / _dpiX;
        double dy = (m.Y - _dragStartCursor.Y) / _dpiY;
        Left = _dragStartLeft + dx;
        Top = _dragStartTop + dy;
        _vx = dx * 6;
    }

    private void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        PetBox.ReleaseMouseCapture();
        _vy = 0; _state = State.Fall;
    }

    private void RegisterClick()
    {
        double t = Now;
        _recentClicks.Add(t);
        _recentClicks.RemoveAll(x => t - x > 1.5);
        if (_recentClicks.Count >= 5) { _recentClicks.Clear(); TickleSpin(); }
    }

    // ------------------------------------------------------------- actions
    private void Happy()
    {
        _happiness = Clamp(_happiness + 10);
        Smile(); Jump(); ShowBubble("♥", 1200);
    }

    private void Feed()
    {
        _hunger = Clamp(_hunger - 25);
        _happiness = Clamp(_happiness + 8);
        Smile(); ShowBubble("Nom nom! 🍪", 1800);
    }

    private void ComeHere()
    {
        _state = State.Chase; _chaseUntil = Now + 4;
        ShowBubble("Coming!", 1500);
    }

    private void TickleSpin()
    {
        ShowBubble("Hehe, that tickles! 🌀", 1800);
        var spin = new RotateTransform();
        PetBox.RenderTransformOrigin = new Point(0.5, 0.9);
        PetBox.RenderTransform = spin;
        var a = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(600)) { RepeatBehavior = new RepeatBehavior(2) };
        a.Completed += (_, _) => PetBox.RenderTransform = null;
        spin.BeginAnimation(RotateTransform.AngleProperty, a);
        _happiness = Clamp(_happiness + 5);
    }

    private void ToggleParty()
    {
        _party = !_party;
        if (_party) ShowBubble("PARTY TIME! ✨", 2000);
        else { _bodyBrush.Color = Color.FromRgb(0x4F, 0xD1, 0xC5); ShowBubble("Phew!", 1200); }
    }

    private void StepParty()
    {
        _partyHue = (_partyHue + 2) % 360;
        _bodyBrush.Color = FromHsv(_partyHue, 0.65, 1);
    }

    // ------------------------------------------------------------- menu
    private void ShowMenu()
    {
        int days = (int)(DateTime.UtcNow - _settings.PetBornUtc).TotalDays;
        var menu = new ContextMenu();
        menu.Items.Add(Header($"🐾 {_name}   Lv.{days + 1}"));
        menu.Items.Add(Header($"♥ {(int)_happiness}   🍽 {100 - (int)_hunger}   ⚡ {(int)_energy}"));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Feed 🍪", (_, _) => Feed()));
        menu.Items.Add(Item("Play 🎾", (_, _) => Happy()));
        menu.Items.Add(Item("Come here 👋", (_, _) => ComeHere()));
        menu.Items.Add(Item("Do a spin 🌀", (_, _) => TickleSpin()));
        menu.Items.Add(Item("Sleep 😴", (_, _) => { _state = State.Sleep; SetMouth(true); ShowBubble("Zzz…", 3000); ScheduleDecision(4, 8); }));
        menu.Items.Add(new Separator());
        var focus = new MenuItem { Header = "Focus timer" };
        focus.Items.Add(Item("25 min (Pomodoro)", (_, _) => StartFocus(25)));
        focus.Items.Add(Item("50 min", (_, _) => StartFocus(50)));
        focus.Items.Add(Item("Stop", (_, _) => { _focusUntil = 0; _state = State.Idle; }));
        menu.Items.Add(focus);
        var rem = new MenuItem { Header = "Health reminders", IsCheckable = true, IsChecked = _settings.PetReminders };
        rem.Click += (_, _) => { _settings.PetReminders = rem.IsChecked; _save(); };
        menu.Items.Add(rem);
        menu.Items.Add(Item("Party mode ✨", (_, _) => ToggleParty()));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item("Rename…", (_, _) => Rename()));
        menu.Items.Add(Item("Hide pet", (_, _) => HiddenByUser?.Invoke()));
        menu.IsOpen = true;
    }

    private static MenuItem Header(string h) => new() { Header = h, IsEnabled = false, FontWeight = FontWeights.Bold };
    private static MenuItem Item(string h, RoutedEventHandler click) { var i = new MenuItem { Header = h }; i.Click += click; return i; }

    private void Rename()
    {
        string input = Microsoft.VisualBasic.Interaction.InputBox("What's my name?", "Rename pet", _name);
        if (!string.IsNullOrWhiteSpace(input))
        {
            _name = input.Trim();
            _settings.PetName = _name; _save();
            ShowBubble($"I'm {_name} now!", 2000);
        }
    }

    // ------------------------------------------------------------- housekeeping
    private void UpdateSuspendState()
    {
        bool fs = Native.IsFullscreenAppForeground();
        if (fs == _suspended) return;
        _suspended = fs;
        if (_suspended) { Visibility = Visibility.Hidden; _timer.Interval = TimeSpan.FromMilliseconds(500); }
        else { Visibility = Visibility.Visible; SendToDesktopLayer(); _timer.Interval = TimeSpan.FromMilliseconds(160); }
    }

    private bool UpdateClickThrough()
    {
        if (_dragging) return true;
        Point c = CursorLocal();
        double cx = Width / 2, cy = Height - 60;
        bool over = (c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy) < 55 * 55;
        SetClickThrough(!over);
        return over;
    }

    private void SetClickThrough(bool on)
    {
        if (on == _clickThrough) return;
        _clickThrough = on;
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
        ex = on ? ex | Native.WS_EX_TRANSPARENT : ex & ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex);
    }

    private void PersistState()
    {
        _settings.PetHappiness = (int)_happiness;
        _settings.PetHunger = (int)_hunger;
        _settings.PetEnergy = (int)_energy;
        _settings.PetLastSeenUtc = DateTime.UtcNow;
        _save();
    }

    public void StopPet()
    {
        _timer.Stop();
        PersistState();
        Close();
    }

    // ------------------------------------------------------------- helpers
    private static Point CursorScreenPx() { var p = Forms.Cursor.Position; return new Point(p.X, p.Y); }
    private Point CursorLocal() { try { return PointFromScreen(CursorScreenPx()); } catch { return new Point(-999, -999); } }

    private static Color FromHsv(double h, double s, double v)
    {
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        double p = v * (1 - s), q = v * (1 - f * s), tt = v * (1 - (1 - f) * s);
        double r = 0, g = 0, b = 0;
        switch (hi)
        {
            case 0: r = v; g = tt; b = p; break;
            case 1: r = q; g = v; b = p; break;
            case 2: r = p; g = v; b = tt; break;
            case 3: r = p; g = q; b = v; break;
            case 4: r = tt; g = p; b = v; break;
            case 5: r = v; g = p; b = q; break;
        }
        return Color.FromRgb((byte)(r * 255), (byte)(g * 255), (byte)(b * 255));
    }
}

/// <summary>Minimal Win32 interop for desktop-layer placement and click-through.</summary>
internal static class Native
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TRANSPARENT = 0x20;
    public const int WS_EX_TOOLWINDOW = 0x80;
    public const int WS_EX_NOACTIVATE = 0x08000000;
    public static readonly IntPtr HWND_BOTTOM = new(1);
    public const uint SWP_NOMOVE = 0x0002, SWP_NOSIZE = 0x0001, SWP_NOACTIVATE = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>True if a non-desktop window is covering the whole primary screen (game/video fullscreen).</summary>
    public static bool IsFullscreenAppForeground()
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == GetDesktopWindow() || fg == GetShellWindow()) return false;
        if (!GetWindowRect(fg, out RECT r)) return false;
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        return w >= Forms.Screen.PrimaryScreen!.Bounds.Width && h >= Forms.Screen.PrimaryScreen!.Bounds.Height;
    }
}
