using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nova.Core;

namespace Nova.Views;

/// <summary>
/// Oyun ya da başka bir uygulama öndeyken bildirim göstermek için, odak çalmayan ve tıklamaları
/// geçiren, her zaman üstte duran küçük pencere (ekran görüntüsü alındı, kupa, süre uyarısı...).
/// </summary>
public sealed class HudWindow : Window
{
    [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr h, int i);
    [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr h, int i, int v);
    const int GWL_EXSTYLE = -20, WS_EX_TRANSPARENT = 0x20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;

    readonly StackPanel _stack = new() { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 30, 30, 0) };
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1) };

    public HudWindow()
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        Focusable = false;
        IsHitTestVisible = false;
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");
        Foreground = Brushes.White;
        var area = SystemParameters.WorkArea;
        Left = area.Right - 520; Top = area.Top; Width = 520; Height = 400;
        Content = _stack;
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            SetWindowLong(h, GWL_EXSTYLE, GetWindowLong(h, GWL_EXSTYLE) | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
        };
        _hide.Tick += (_, _) => { if (_stack.Children.Count == 0) { _hide.Stop(); Hide(); } };
    }

    public void Notify(string glyph, string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(Ui.Glyph(glyph, 22));
        var tb = Ui.Text(L.T(text), 18);
        tb.TextWrapping = TextWrapping.Wrap;
        tb.MaxWidth = 380;
        tb.Margin = new Thickness(14, 0, 0, 2);
        row.Children.Add(tb);
        var card = new Border
        {
            CornerRadius = new CornerRadius(16),
            Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x16, 0x1E, 0x33)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(20, 16, 22, 16),
            Margin = new Thickness(0, 0, 0, 10),
            HorizontalAlignment = HorizontalAlignment.Right,
            Child = row,
            Opacity = 0,
        };
        _stack.Children.Insert(0, card);
        while (_stack.Children.Count > 3) _stack.Children.RemoveAt(_stack.Children.Count - 1);
        if (!IsVisible) Show();
        _hide.Start();

        var a = new DoubleAnimationUsingKeyFrames();
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(200))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4))));
        a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(4.5))));
        a.Completed += (_, _) => _stack.Children.Remove(card);
        card.BeginAnimation(OpacityProperty, a);
    }
}
