using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Forms = System.Windows.Forms;
using Point = System.Windows.Point;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;

namespace LiveWallpaper;

/// <summary>
/// A vector "desktop pet" that wanders the screen, idles, reacts to the mouse,
/// can be dragged (with gravity), and hides a few easter eggs.
/// </summary>
public partial class PetWindow : Window
{
    private enum State { Idle, Walk, Sit, Sleep, Jump, Fall, Drag, Chase }

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Random _rng = new();

    private State _state = State.Idle;
    private double _lastSeconds;
    private double _vx;                 // horizontal speed, DIP/s
    private double _vy;                 // vertical speed, DIP/s (jump/fall)
    private double _nextDecision;       // time (s) of next behavior change
    private double _nextBlink;
    private double _walkPhase;

    // interaction
    private bool _dragging;
    private Point _dragOffset;
    private Point _lastMouse;
    private readonly List<double> _recentClicks = new();

    // pupils / eyes
    private readonly TranslateTransform _pupil = new();
    private readonly ScaleTransform _leftBlink = new(1, 1);
    private readonly ScaleTransform _rightBlink = new(1, 1);

    // stats & flags
    private int _happiness = 70;
    private int _hunger = 30;
    private bool _party;
    private double _partyHue;
    private double _bubbleHideAt = -1;
    private double _chaseUntil;

    private string _name = "Pixel";
    public event Action? HiddenByUser;

    private readonly string[] _quips =
    {
        "Hi there!", "What'cha doing?", "Boop!", "I'm bored…", "Wheee!",
        "Feed me? 🍪", "You're doing great!", "Zzz…", "Let's play!", "Nice desktop!",
    };

    public PetWindow(string? name = null)
    {
        InitializeComponent();
        if (!string.IsNullOrWhiteSpace(name)) _name = name!;

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

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        SnapToGround();
        Left = Math.Max(WorkArea.Left, Math.Min(Left, WorkArea.Right - Width));
        _nextDecision = Now + 1;
        _nextBlink = Now + 2;
        ShowBubble($"Hi, I'm {_name}!", 2500);
        _timer.Tick += Tick;
        _timer.Start();
    }

    private double Now => _clock.Elapsed.TotalSeconds;
    private static Rect WorkArea => SystemParameters.WorkArea;
    private double Ground => WorkArea.Bottom - Height;

    private void SnapToGround() => Top = Ground;

    // ----------------------------------------------------------------- main loop
    private void Tick(object? sender, EventArgs e)
    {
        double t = Now;
        double dt = Math.Min(0.05, t - _lastSeconds);
        _lastSeconds = t;

        Blink(t);
        FollowCursorWithEyes();
        if (_party) StepParty();

        switch (_state)
        {
            case State.Idle: case State.Sit: case State.Sleep: StepIdle(t, dt); break;
            case State.Walk: StepWalk(dt); break;
            case State.Chase: StepChase(t, dt); break;
            case State.Jump: case State.Fall: StepAirborne(dt); break;
            case State.Drag: /* driven by mouse */ break;
        }

        if (_bubbleHideAt > 0 && t >= _bubbleHideAt)
        {
            Bubble.Visibility = Visibility.Collapsed;
            _bubbleHideAt = -1;
        }

        // gentle idle bob
        if (_state is State.Idle or State.Walk)
            Bob.Y = Math.Sin(t * (_state == State.Walk ? 12 : 2.2)) * (_state == State.Walk ? 3 : 1.5);
        else if (_state is not State.Jump and not State.Fall and not State.Drag)
            Bob.Y = 0;
    }

    private void StepIdle(double t, double dt)
    {
        if (t >= _nextDecision) Decide();
        // slowly get hungrier / less happy over time
        if (_rng.NextDouble() < dt * 0.05) { _hunger = Math.Min(100, _hunger + 1); }
    }

    private void StepWalk(double dt)
    {
        Left += _vx * dt;
        _walkPhase += dt;
        Facing.ScaleX = _vx < 0 ? -1 : 1;

        if (Left <= WorkArea.Left) { Left = WorkArea.Left; _vx = Math.Abs(_vx); }
        if (Left >= WorkArea.Right - Width) { Left = WorkArea.Right - Width; _vx = -Math.Abs(_vx); }

        if (Now >= _nextDecision) Decide();
    }

    private void StepChase(double t, double dt)
    {
        double targetX = MouseScreen().X - Width / 2;
        double dx = targetX - Left;
        _vx = Math.Sign(dx) * 220;
        if (Math.Abs(dx) > 4) Left += _vx * dt;
        Facing.ScaleX = dx < 0 ? -1 : 1;
        Bob.Y = Math.Sin(t * 14) * 3;
        if (t >= _chaseUntil) { _vx = 0; SetState(State.Idle); ScheduleDecision(1, 3); }
    }

    private void StepAirborne(double dt)
    {
        _vy += 1600 * dt;              // gravity
        Top += _vy * dt;
        Left += _vx * dt;
        if (Left < WorkArea.Left) Left = WorkArea.Left;
        if (Left > WorkArea.Right - Width) Left = WorkArea.Right - Width;

        if (Top >= Ground)
        {
            Top = Ground;
            _vy = 0; _vx = 0;
            Squish();
            SetState(State.Idle);
            ScheduleDecision(1, 3);
        }
    }

    // ----------------------------------------------------------------- behaviors
    private void Decide()
    {
        int roll = _rng.Next(100);
        if (roll < 35) { SetState(State.Walk); _vx = (_rng.Next(2) == 0 ? -1 : 1) * _rng.Next(45, 85); ScheduleDecision(2, 5); }
        else if (roll < 60) { SetState(State.Idle); _vx = 0; ScheduleDecision(2, 4); }
        else if (roll < 75) { SetState(State.Sit); _vx = 0; ScheduleDecision(3, 6); }
        else if (roll < 85) { SetState(State.Sleep); _vx = 0; SetMouth(sleepy: true); ShowBubble("Zzz…", 2500); ScheduleDecision(4, 8); }
        else if (roll < 95) { Jump(); ScheduleDecision(1, 2); }
        else { if (_rng.Next(3) == 0) Say(); ScheduleDecision(2, 4); }

        if (_state != State.Sleep) SetMouth(sleepy: false);
    }

    private void ScheduleDecision(double min, double max) => _nextDecision = Now + min + _rng.NextDouble() * (max - min);

    private void SetState(State s) => _state = s;

    private void Jump()
    {
        if (_state is State.Jump or State.Fall or State.Drag) return;
        SetState(State.Jump);
        _vy = -600;
        _vx = (_rng.Next(3) - 1) * 60;
    }

    private void Squish()
    {
        var sx = new DoubleAnimation(1.25, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        var sy = new DoubleAnimation(0.75, 1, TimeSpan.FromMilliseconds(220)) { EasingFunction = new ElasticEase { Oscillations = 1, Springiness = 4 } };
        Squash.BeginAnimation(ScaleTransform.ScaleXProperty, sx);
        Squash.BeginAnimation(ScaleTransform.ScaleYProperty, sy);
    }

    // ----------------------------------------------------------------- face
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
        Point c = MouseScreen();
        double cx = Left + Width / 2, cy = Top + Height / 2;
        double ang = Math.Atan2(c.Y - cy, c.X - cx);
        double dist = Math.Min(2.5, Math.Sqrt((c.X - cx) * (c.X - cx) + (c.Y - cy) * (c.Y - cy)) / 60);
        _pupil.X = Math.Cos(ang) * dist;
        _pupil.Y = Math.Sin(ang) * dist;
    }

    private void SetMouth(bool sleepy)
    {
        Mouth.Data = sleepy
            ? Geometry.Parse("M54,86 L66,86")
            : Geometry.Parse("M54,84 Q60,90 66,84");
    }

    private void Smile()
    {
        Mouth.Data = Geometry.Parse("M52,82 Q60,94 68,82");
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { SetMouth(sleepy: false); t.Stop(); };
        t.Start();
    }

    // ----------------------------------------------------------------- speech
    private void Say() => ShowBubble(_quips[_rng.Next(_quips.Length)], 2200);

    private void ShowBubble(string text, int ms)
    {
        BubbleText.Text = text;
        Bubble.Visibility = Visibility.Visible;
        _bubbleHideAt = Now + ms / 1000.0;
    }

    // ----------------------------------------------------------------- mouse
    private void OnPetMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { Happy(); return; }
        RegisterClick();

        _dragging = true;
        _dragOffset = e.GetPosition(this);
        _lastMouse = MouseScreen();
        SetState(State.Drag);
        Bob.Y = 0;
        PetBox.CaptureMouse();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        Point m = MouseScreen();
        Left = m.X - _dragOffset.X;
        Top = m.Y - _dragOffset.Y;
        _vx = (m.X - _lastMouse.X) * 12;   // remember toss velocity
        _lastMouse = m;
    }

    private void OnPetMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        PetBox.ReleaseMouseCapture();
        _vy = 0;
        SetState(State.Fall);              // drop with gravity
    }

    private void RegisterClick()
    {
        double t = Now;
        _recentClicks.Add(t);
        _recentClicks.RemoveAll(x => t - x > 1.5);
        if (_recentClicks.Count >= 5)      // easter egg: tickle
        {
            _recentClicks.Clear();
            TickleSpin();
        }
    }

    // ----------------------------------------------------------------- actions
    private void Happy()
    {
        _happiness = Math.Min(100, _happiness + 10);
        Smile();
        Jump();
        ShowBubble("♥", 1200);
    }

    private void Feed()
    {
        _hunger = Math.Max(0, _hunger - 25);
        _happiness = Math.Min(100, _happiness + 8);
        Smile();
        ShowBubble("Nom nom! 🍪", 1800);
    }

    private void ComeHere()
    {
        SetState(State.Chase);
        _chaseUntil = Now + 4;
        ShowBubble("Coming!", 1500);
    }

    private void TickleSpin()   // easter egg
    {
        ShowBubble("Hehe that tickles! 🌀", 1800);
        var spin = new RotateTransform();
        PetBox.RenderTransformOrigin = new Point(0.5, 0.9);
        PetBox.RenderTransform = spin;
        var a = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(600)) { RepeatBehavior = new RepeatBehavior(2) };
        a.Completed += (_, _) => PetBox.RenderTransform = null;
        spin.BeginAnimation(RotateTransform.AngleProperty, a);
        _happiness = Math.Min(100, _happiness + 5);
    }

    private void ToggleParty()  // easter egg
    {
        _party = !_party;
        if (_party) ShowBubble("PARTY TIME! ✨", 2000);
        else { Body.Fill = new SolidColorBrush(Color.FromRgb(0x4F, 0xD1, 0xC5)); ShowBubble("Phew!", 1200); }
    }

    private void StepParty()
    {
        _partyHue = (_partyHue + 2) % 360;
        Body.Fill = new SolidColorBrush(FromHsv(_partyHue, 0.65, 1));
    }

    // ----------------------------------------------------------------- menu
    private void ShowMenu()
    {
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem($"🐾 {_name}  (♥{_happiness}  🍽{100 - _hunger})", null, isHeader: true));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Feed 🍪", (_, _) => Feed()));
        menu.Items.Add(MenuItem("Play 🎾", (_, _) => Happy()));
        menu.Items.Add(MenuItem("Come here 👋", (_, _) => ComeHere()));
        menu.Items.Add(MenuItem("Sleep 😴", (_, _) => { SetState(State.Sleep); SetMouth(true); ShowBubble("Zzz…", 3000); ScheduleDecision(4, 8); }));
        menu.Items.Add(MenuItem("Party mode ✨", (_, _) => ToggleParty()));
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem("Rename…", (_, _) => Rename()));
        menu.Items.Add(MenuItem("Hide pet", (_, _) => HiddenByUser?.Invoke()));
        menu.IsOpen = true;
    }

    private static MenuItem MenuItem(string header, RoutedEventHandler? click, bool isHeader = false)
    {
        var item = new MenuItem { Header = header };
        if (isHeader) { item.IsEnabled = false; item.FontWeight = FontWeights.Bold; }
        if (click != null) item.Click += click;
        return item;
    }

    private void Rename()
    {
        var input = Microsoft.VisualBasic.Interaction.InputBox("What's my name?", "Rename pet", _name);
        if (!string.IsNullOrWhiteSpace(input))
        {
            _name = input.Trim();
            ShowBubble($"I'm {_name} now!", 2000);
            NameChanged?.Invoke(_name);
        }
    }

    public event Action<string>? NameChanged;

    // ----------------------------------------------------------------- helpers
    private static Point MouseScreen()
    {
        var p = Forms.Cursor.Position;   // physical pixels; close enough for a pet
        return new Point(p.X, p.Y);
    }

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

    public void StopPet()
    {
        _timer.Stop();
        Close();
    }
}
