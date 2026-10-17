using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Nova.Core;

namespace Nova.Views;

/// <summary>Masaüstü modunun pencereleri için ortak ayarlar: her zaman üstte, odak çalmayan araç penceresi.</summary>
public abstract class FloatingWindow : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    protected FloatingWindow(bool clickThrough)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = Brushes.White;
        if (clickThrough) IsHitTestVisible = false;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            int ex = GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | (clickThrough ? WS_EX_TRANSPARENT : 0);
            SetWindowLong(h, GWL_EXSTYLE, ex);
        };
        SizeChanged += (_, _) => Place();
    }

    protected abstract void Place();

    protected static Border Panel(UIElement child, Thickness padding, double radius) => new()
    {
        CornerRadius = new CornerRadius(radius),
        Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x12, 0x19, 0x2C)),
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
        BorderThickness = new Thickness(1),
        Padding = padding,
        Child = child,
    };
}

/// <summary>Masaüstü modu açıkken ekranın üstünde duran, tıklamaları geçiren tuş ipucu şeridi.</summary>
public sealed class DesktopHint : FloatingWindow
{
    readonly StackPanel _row = new() { Orientation = Orientation.Horizontal };

    public DesktopHint() : base(clickThrough: true)
    {
        Content = Panel(_row, new Thickness(22, 12, 24, 12), 22);
        SetKeyboard(false);
    }

    protected override void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Top + 18;
    }

    public void SetKeyboard(bool open)
    {
        _row.Children.Clear();
        var title = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 18, 0) };
        title.Children.Add(Ui.Glyph(open ? Glyphs.Keyboard : Glyphs.Mouse, 18));
        var t = Ui.Text(open ? "Ekran klavyesi" : "Masaüstü modu", 16, weight: FontWeights.SemiBold);
        t.Margin = new Thickness(10, 0, 0, 1);
        title.Children.Add(t);
        _row.Children.Add(title);

        var hints = open
            ? new[] { ("A", "Bas"), ("B", "Klavyeyi kapat"), ("X", "Sil"), ("Y", "Boşluk") }
            : new[] { ("A", "Tıkla"), ("B", "Sağ tık"), ("X", "Klavye"), ("Y", "Enter"), ("LB RB", "Pencere değiştir"), ("R", "Kaydır"), ("LT", "Yavaş"), ("View + R3", "Çık") };
        foreach (var (button, label) in hints)
        {
            var key = new Border
            {
                CornerRadius = new CornerRadius(7),
                Background = new SolidColorBrush(Color.FromArgb(0x33, 255, 255, 255)),
                Padding = new Thickness(7, 1, 7, 2),
                Margin = new Thickness(14, 0, 7, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Child = Ui.Text(button, 13, weight: FontWeights.SemiBold),
            };
            _row.Children.Add(key);
            var l = Ui.Text(label, 15, Ui.Dim);
            l.VerticalAlignment = VerticalAlignment.Center;
            _row.Children.Add(l);
        }
    }
}

/// <summary>
/// Sistem genelinde ekran klavyesi: öne geçmeden (WS_EX_NOACTIVATE) altta durur, basılan tuşlar
/// SendInput ile öndeki pencereye (ör. oyun başlatıcısının giriş penceresi) yazılır.
/// </summary>
public sealed class SystemKeyboard : FloatingWindow
{
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetWindowText(IntPtr h, System.Text.StringBuilder s, int n);

    readonly TextBlock _target = Ui.Text("", 16, Ui.Dim);

    public OnScreenKeyboard Keys { get; } = new() { SystemKeys = true, DoneLabel = "Kapat" };

    public SystemKeyboard() : base(clickThrough: false)
    {
        var stack = new StackPanel();
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(4, 0, 0, 16) };
        head.Children.Add(Ui.Glyph(Glyphs.Keyboard, 20));
        var title = Ui.Text("Öndeki pencereye yazar", 17, weight: FontWeights.SemiBold);
        title.Margin = new Thickness(12, 0, 14, 1);
        head.Children.Add(title);
        _target.TextTrimming = TextTrimming.CharacterEllipsis;
        _target.MaxWidth = 460;
        head.Children.Add(_target);
        stack.Children.Add(head);
        stack.Children.Add(Keys);
        Content = Panel(stack, new Thickness(26, 22, 18, 16), 28);

        Keys.Typed += Injector.Type;
        Keys.Backspaced += () => Injector.Press(Injector.VK_BACK);
        Keys.Special += k => Injector.Press(k == "tab" ? Injector.VK_TAB : Injector.VK_RETURN);
    }

    protected override void Place()
    {
        var area = SystemParameters.WorkArea;
        Left = area.Left + (area.Width - ActualWidth) / 2;
        Top = area.Bottom - ActualHeight - 24;
    }

    public void Open()
    {
        var sb = new System.Text.StringBuilder(256);
        GetWindowText(GetForegroundWindow(), sb, sb.Capacity);
        _target.Text = sb.ToString();
        Keys.ShowsSelection = true;
        Show();
    }
}
