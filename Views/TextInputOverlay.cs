using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nova.Core;

namespace Nova.Views;

/// <summary>Yeniden adlandırma, yeni klasör, API anahtarı gibi metin girişleri için katman.</summary>
public sealed class TextInputOverlay : Grid, IOverlay, ITextTarget
{
    readonly Action<string> _submit;
    readonly TextBlock _text;
    readonly OnScreenKeyboard _osk;
    readonly bool _secret;
    string _value;
    bool _closing;

    public event Action? Closed;

    public TextInputOverlay(string title, string initial, Action<string> submit, string? help = null, bool secret = false)
    {
        _submit = submit;
        _value = initial;
        _secret = secret;
        Background = Ui.Backdrop;

        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Width = 900 };
        panel.Children.Add(Ui.Text(title, 40, weight: FontWeights.Light));
        if (help != null)
        {
            var h = Ui.Text(help, 19, Ui.Dim);
            h.TextWrapping = TextWrapping.Wrap;
            h.Margin = new Thickness(0, 10, 0, 0);
            panel.Children.Add(h);
        }

        _text = Ui.Text("", 30);
        var caret = new Border { Width = 2, Height = 34, Background = Brushes.White, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        caret.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(550)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(_text);
        line.Children.Add(caret);
        panel.Children.Add(new Border
        {
            Height = 84,
            CornerRadius = new CornerRadius(42),
            Background = Ui.CardBg,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(36, 0, 36, 0),
            Margin = new Thickness(0, 30, 0, 36),
            Child = line,
        });

        _osk = new OnScreenKeyboard { HorizontalAlignment = HorizontalAlignment.Center };
        _osk.Typed += TypeText;
        _osk.Backspaced += Backspace;
        _osk.Done += Submit;
        panel.Children.Add(_osk);

        canvas.Children.Add(panel);
        Render();
        Loaded += (_, _) => Ui.FadeIn(panel);
    }

    public IEnumerable<(NavAction, string)> Hints => new[]
    {
        (NavAction.Confirm, "Tuşa bas"), (NavAction.Search, "Sil"), (NavAction.Options, "Boşluk"), (NavAction.Back, "İptal"),
    };

    public bool Handle(NavAction a)
    {
        if (a == NavAction.Back) { Close(); return true; }
        return _osk.Handle(a);
    }

    public void TypeText(string text) { _value += text; Render(); }

    public void Backspace()
    {
        if (_value.Length == 0) return;
        _value = _value[..^1];
        Render();
    }

    public void Submit()
    {
        var v = _value.Trim();
        Close();
        if (v.Length > 0) _submit(v);
    }

    void Render() => _text.Text = _secret && _value.Length > 4 ? new string('•', _value.Length - 4) + _value[^4..] : _value;

    void Close()
    {
        if (_closing) return;
        _closing = true;
        Ui.FadeOut(this, () => Closed?.Invoke());
    }
}
