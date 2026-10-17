using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nova.Models;

namespace Nova.Views;

/// <summary>Kodla kurulan katmanlar için küçük arayüz yardımcıları.</summary>
public static class Ui
{
    public static readonly Brush Dim = (Brush)Application.Current.FindResource("TextDim");
    public static readonly Brush Faint = (Brush)Application.Current.FindResource("TextFaint");
    public static readonly Brush Ink = Freeze(new SolidColorBrush(Color.FromRgb(0x0B, 0x0F, 0x1A)));
    public static readonly Brush CardBg = Freeze(new SolidColorBrush(Color.FromArgb(0x1F, 255, 255, 255)));
    public static readonly Brush PanelBg = Freeze(new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x16, 0x26)));
    public static readonly Brush Backdrop = Freeze(new SolidColorBrush(Color.FromArgb(0xF7, 0x05, 0x08, 0x12)));
    public static readonly Brush Line = Freeze(new SolidColorBrush(Color.FromArgb(0x24, 255, 255, 255)));

    static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static TextBlock Text(string text, double size, Brush? fg = null, FontWeight? weight = null) => new()
    {
        Text = text,
        FontSize = size,
        Foreground = fg ?? Brushes.White,
        FontWeight = weight ?? FontWeights.Normal,
        TextTrimming = TextTrimming.CharacterEllipsis,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Glyph(string glyph, double size, Brush? fg = null) => new()
    {
        Text = glyph,
        FontSize = size,
        Style = (Style)Application.Current.FindResource("GlyphText"),
        Foreground = fg ?? Brushes.White,
    };

    /// <summary>Bir TileItem'ın küçük resmini/simgesini/glifini gösteren kare.</summary>
    public static FrameworkElement TileIcon(TileItem item, double size)
    {
        var g = new Grid { Width = size, Height = size };
        g.Children.Add(new Border { CornerRadius = new CornerRadius(size * 0.14), Background = item.TileBrush ?? CardBg });
        var brush = new ImageBrush { Stretch = Stretch.UniformToFill };
        BindingOperations.SetBinding(brush, ImageBrush.ImageSourceProperty, new Binding(nameof(TileItem.FillImage)) { Source = item });
        g.Children.Add(new Border { CornerRadius = new CornerRadius(size * 0.14), Background = brush });
        var img = new Image { Margin = new Thickness(size * 0.18) };
        img.SetBinding(Image.SourceProperty, new Binding(nameof(TileItem.IconImage)) { Source = item });
        g.Children.Add(img);
        var glyph = Glyph("", size * 0.42);
        glyph.SetBinding(TextBlock.TextProperty, new Binding(nameof(TileItem.Glyph)) { Source = item });
        g.Children.Add(glyph);
        return g;
    }

    /// <summary>Seçili kart: beyaz çerçeve ve parıltı.</summary>
    public static void SetRing(Border b, bool on)
    {
        b.BorderBrush = on ? Brushes.White : Brushes.Transparent;
        b.Effect = on ? new System.Windows.Media.Effects.DropShadowEffect { Color = Colors.White, BlurRadius = 24, ShadowDepth = 0, Opacity = 0.45 } : null;
    }

    public static void FadeIn(UIElement e, double fromY = 30)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220)));
        if (fromY != 0)
        {
            var t = new TranslateTransform();
            e.RenderTransform = t;
            t.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(fromY, 0, TimeSpan.FromMilliseconds(340)) { EasingFunction = ease });
        }
    }

    public static void FadeOut(UIElement e, Action done)
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(160));
        a.Completed += (_, _) => done();
        e.BeginAnimation(UIElement.OpacityProperty, a);
    }

    /// <summary>1920x1080 tasarım tuvali, her ekrana ölçeklenir.</summary>
    public static (Viewbox Box, Grid Canvas) Stage()
    {
        var canvas = new Grid { Width = 1920, Height = 1080 };
        return (new Viewbox { Stretch = Stretch.Uniform, Child = canvas }, canvas);
    }
}
