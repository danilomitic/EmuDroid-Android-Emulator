using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace EmuDroid.Keymap;

/// <summary>
/// Transparent window over the emulator. In play mode it is click-through and only shows faint key hints.
/// In edit mode it dims the screen and lets the user place, move, bind and delete controls.
/// </summary>
internal sealed class OverlayWindow : Window
{
    private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0x7F, 0x9B, 0xFF));
    private static readonly Brush Hint = new SolidColorBrush(Color.FromArgb(0x55, 0x10, 0x14, 0x2B));
    private static readonly Brush HintStroke = new SolidColorBrush(Color.FromArgb(0x99, 0xE7, 0xEB, 0xF3));
    private static readonly Brush EditFill = new SolidColorBrush(Color.FromArgb(0xCC, 0x28, 0x30, 0x41));
    private static readonly Brush Dim = new SolidColorBrush(Color.FromArgb(0x88, 0x0B, 0x0E, 0x16));

    private readonly KeymapSession _session;
    private readonly Canvas _canvas = new();
    private readonly Border _toolbar;
    private readonly TextBlock _status = new() { Foreground = Brushes.White, FontSize = 12, Margin = new Thickness(8, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
    private readonly CheckBox _onlyThisApp = new() { Content = "Only for this app", Foreground = Brushes.White, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 8, 0) };
    private KeymapProfile _draft = new();
    private KeyControl? _selected;
    private KeyControl? _dragging;
    private Point _dragOffset;
    private bool _closing;

    public OverlayWindow(KeymapSession session)
    {
        _session = session;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "EmuDroid controls";

        _toolbar = BuildToolbar();
        var root = new Grid();
        root.Children.Add(_canvas);
        root.Children.Add(_toolbar);
        Content = root;

        SourceInitialized += (_, _) => Native.MakeClickThrough(new WindowInteropHelper(this).Handle, true);
        SizeChanged += (_, _) => Refresh();
        KeyDown += OnEditKeyDown;
        Closed += (_, _) => _session.OnOverlayClosed();
        Refresh();
    }

    public bool IsEditing { get; private set; }

    public void PlaceOver(int left, int top, int width, int height)
    {
        // Win32 gives physical pixels; WPF wants device-independent units.
        var source = PresentationSource.FromVisual(this);
        double sx = source?.CompositionTarget.TransformFromDevice.M11 ?? 1;
        double sy = source?.CompositionTarget.TransformFromDevice.M22 ?? 1;
        Left = left * sx;
        Top = top * sy;
        Width = Math.Max(1, width * sx);
        Height = Math.Max(1, height * sy);
    }

    public void BeginEdit()
    {
        if (!IsVisible) Show();
        IsEditing = true;
        _draft = Clone(_session.Profile);
        _selected = null;
        Native.MakeClickThrough(new WindowInteropHelper(this).Handle, false);
        Activate();
        Focus();
        Refresh();
    }

    private void EndEdit(bool save)
    {
        if (save) _session.SaveProfile(_draft, _onlyThisApp.IsChecked == true);
        IsEditing = false;
        _selected = null;
        Native.MakeClickThrough(new WindowInteropHelper(this).Handle, true);
        Refresh();
    }

    public void Refresh()
    {
        _canvas.Children.Clear();
        _canvas.Background = IsEditing ? Dim : Brushes.Transparent;
        _toolbar.Visibility = IsEditing ? Visibility.Visible : Visibility.Collapsed;
        var profile = IsEditing ? _draft : _session.Profile;

        if (!IsEditing && !_session.Enabled) return;

        foreach (var c in profile.Controls) _canvas.Children.Add(BuildControl(c));

        if (IsEditing)
        {
            _status.Text = _selected is null
                ? $"Profile: {_session.ForegroundPackage ?? "default"}. Add a control, drag it into place, click it and press a key. Right-click deletes."
                : $"Press a key for “{_selected.Label}”. Right-click to delete.";
            _onlyThisApp.IsEnabled = _session.ForegroundPackage is not null;
        }
    }

    private FrameworkElement BuildControl(KeyControl c)
    {
        double w = ActualWidth > 0 ? ActualWidth : Width;
        double h = ActualHeight > 0 ? ActualHeight : Height;
        double size = c.Type is ControlType.Joystick or ControlType.Aim ? c.Radius * 2 * w : 44;
        size = Math.Max(size, 36);

        var circle = new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            BorderThickness = new Thickness(IsEditing && c == _selected ? 3 : 1.5),
            BorderBrush = IsEditing && c == _selected ? Accent : HintStroke,
            Background = IsEditing ? EditFill : Hint,
            Child = new TextBlock
            {
                Text = c.Label,
                Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
            Tag = c,
            Opacity = IsEditing ? 1 : 0.75,
            Cursor = IsEditing ? Cursors.SizeAll : null,
        };
        Canvas.SetLeft(circle, c.X * w - size / 2);
        Canvas.SetTop(circle, c.Y * h - size / 2);

        if (IsEditing)
        {
            circle.MouseLeftButtonDown += (_, e) =>
            {
                _selected = c;
                _dragging = c;
                _dragOffset = e.GetPosition(circle);
                circle.CaptureMouse();
                Refresh();
                e.Handled = true;
            };
            circle.MouseMove += (_, e) =>
            {
                if (_dragging != c || e.LeftButton != MouseButtonState.Pressed) return;
                var p = e.GetPosition(_canvas);
                c.X = Math.Clamp((p.X - _dragOffset.X + size / 2) / w, 0, 1);
                c.Y = Math.Clamp((p.Y - _dragOffset.Y + size / 2) / h, 0, 1);
                Canvas.SetLeft(circle, c.X * w - size / 2);
                Canvas.SetTop(circle, c.Y * h - size / 2);
            };
            circle.MouseLeftButtonUp += (_, _) => { _dragging = null; circle.ReleaseMouseCapture(); };
            circle.MouseRightButtonUp += (_, e) =>
            {
                _draft.Controls.Remove(c);
                if (_selected == c) _selected = null;
                Refresh();
                e.Handled = true;
            };
        }
        return circle;
    }

    private void OnEditKeyDown(object sender, KeyEventArgs e)
    {
        if (!IsEditing) return;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Escape)
        {
            if (_selected is not null) { _selected = null; Refresh(); }
            else EndEdit(save: false);
            e.Handled = true;
            return;
        }
        if (_selected is { Type: ControlType.Tap })
        {
            _selected.Key = key.ToString();
            Refresh();
            e.Handled = true;
        }
    }

    private Border BuildToolbar()
    {
        var panel = new WrapPanel { Margin = new Thickness(6) };
        void Add(string text, Action onClick, bool primary = false)
        {
            var b = new Button
            {
                Content = text,
                Margin = new Thickness(4),
                Padding = new Thickness(12, 6, 12, 6),
                Background = primary ? Accent : new SolidColorBrush(Color.FromRgb(0x28, 0x30, 0x41)),
                Foreground = primary ? new SolidColorBrush(Color.FromRgb(0x10, 0x14, 0x2B)) : Brushes.White,
                BorderThickness = new Thickness(0),
                FontWeight = FontWeights.SemiBold,
                Cursor = Cursors.Hand,
            };
            b.Click += (_, _) => onClick();
            panel.Children.Add(b);
        }

        Add("Add key", () => AddControl(new KeyControl { Type = ControlType.Tap, Key = "?", X = 0.8, Y = 0.6 }));
        Add("Add joystick", () => AddControl(new KeyControl { Type = ControlType.Joystick, X = 0.18, Y = 0.72, Radius = 0.09 }), primary: false);
        Add("Add aim", () => AddControl(new KeyControl { Type = ControlType.Aim, X = 0.65, Y = 0.5, Radius = 0.12 }, unique: true));
        Add("Add fire", () => AddControl(new KeyControl { Type = ControlType.Fire, X = 0.88, Y = 0.72 }, unique: true));
        panel.Children.Add(_onlyThisApp);
        Add("Cancel", () => EndEdit(save: false));
        Add("Save", () => EndEdit(save: true), primary: true);
        Add("Turn off controls", () => _session.Close());

        var stack = new StackPanel();
        stack.Children.Add(panel);
        stack.Children.Add(_status);
        return new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xEE, 0x1F, 0x25, 0x32)),
            CornerRadius = new CornerRadius(10),
            Margin = new Thickness(12),
            Padding = new Thickness(4, 4, 4, 8),
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalAlignment = HorizontalAlignment.Center,
            Child = stack,
        };
    }

    private void AddControl(KeyControl c, bool unique = false)
    {
        if (unique) _draft.Controls.RemoveAll(x => x.Type == c.Type);
        _draft.Controls.Add(c);
        _selected = c;
        Refresh();
    }

    private static KeymapProfile Clone(KeymapProfile p) => new()
    {
        PackageName = p.PackageName,
        AimToggle = p.AimToggle,
        AimSensitivity = p.AimSensitivity,
        Controls = p.Controls.Select(c => new KeyControl
        {
            Type = c.Type, Key = c.Key, X = c.X, Y = c.Y, Radius = c.Radius, Up = c.Up, Left = c.Left, Down = c.Down, Right = c.Right,
        }).ToList(),
    };

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _closing = true;
        base.OnClosing(e);
    }

    public new void Hide()
    {
        if (!_closing) base.Hide();
    }
}
