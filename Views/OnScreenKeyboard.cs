using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nova.Core;

namespace Nova.Views;

/// <summary>Oyun koluyla yazı yazmak için Türkçe Q ekran klavyesi.</summary>
public sealed class OnScreenKeyboard : StackPanel
{
    sealed class KeyDef
    {
        public string Text = "";
        public string Kind = "char"; // char, shift, space, back, done
        public double Units = 1;
        public Border View = null!;
        public TextBlock Label = null!;
        public double Center;
    }

    const double Unit = 62, Gap = 8, KeyH = 58;

    readonly List<List<KeyDef>> _rows = new();
    int _r = 1, _c = 0;
    bool _shift, _focused = true;

    public event Action<string>? Typed;
    public event Action? Backspaced;
    public event Action? Done;

    public string DoneLabel { get; init; } = "Tamam";

    public OnScreenKeyboard()
    {
        Loaded += (_, _) => { if (_rows.Count == 0) Build(); };
    }

    public bool IsKeyboardFocused
    {
        get => _focused;
        set { _focused = value; Refresh(); }
    }

    void Build()
    {
        string[] lines = { "1234567890", "qwertyuıopğü", "asdfghjklşi", "zxcvbnmöç.-" };
        foreach (var line in lines)
            _rows.Add(line.Select(ch => new KeyDef { Text = ch.ToString() }).ToList());
        _rows.Add(new List<KeyDef>
        {
            new() { Text = Glyphs.Shift, Kind = "shift", Units = 2 },
            new() { Text = "Boşluk", Kind = "space", Units = 6 },
            new() { Text = Glyphs.Backspace, Kind = "back", Units = 2 },
            new() { Text = DoneLabel, Kind = "done", Units = 2 },
        });

        double maxWidth = _rows.Max(RowWidth);
        foreach (var row in _rows)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, Gap) };
            double x = (maxWidth - RowWidth(row)) / 2;
            foreach (var k in row)
            {
                double w = k.Units * Unit + (k.Units - 1) * Gap;
                k.Center = x + w / 2;
                x += w + Gap;
                bool glyph = k.Kind is "shift" or "back";
                k.Label = glyph ? Ui.Glyph(k.Text, 22) : Ui.Text(k.Text, k.Kind == "char" ? 24 : 19);
                k.Label.HorizontalAlignment = HorizontalAlignment.Center;
                k.View = new Border
                {
                    Width = w,
                    Height = KeyH,
                    Margin = new Thickness(0, 0, Gap, 0),
                    CornerRadius = new CornerRadius(12),
                    Child = k.Label,
                    Cursor = Cursors.Hand,
                };
                var key = k;
                k.View.MouseLeftButtonUp += (_, e) =>
                {
                    (_r, _c) = Find(key);
                    Sfx.Move();
                    Press(key);
                    e.Handled = true;
                };
                panel.Children.Add(k.View);
            }
            Children.Add(panel);
        }
        Refresh();
    }

    static double RowWidth(List<KeyDef> row) => row.Sum(k => k.Units * Unit + (k.Units - 1) * Gap) + (row.Count - 1) * Gap;

    (int, int) Find(KeyDef k)
    {
        for (int r = 0; r < _rows.Count; r++)
        {
            int c = _rows[r].IndexOf(k);
            if (c >= 0) return (r, c);
        }
        return (_r, _c);
    }

    /// <summary>Klavye içinde gezinir; kenardan çıkılmak istenirse false döner.</summary>
    public bool Handle(NavAction a)
    {
        if (_rows.Count == 0) Build();
        switch (a)
        {
            case NavAction.Left:
                if (_c == 0) return false;
                _c--; break;
            case NavAction.Right:
                if (_c >= _rows[_r].Count - 1) return false;
                _c++; break;
            case NavAction.Up:
            case NavAction.Down:
                int nr = _r + (a == NavAction.Up ? -1 : 1);
                if (nr < 0 || nr >= _rows.Count) return false;
                double cx = _rows[_r][_c].Center;
                _c = _rows[nr].Select((k, i) => (d: Math.Abs(k.Center - cx), i)).MinBy(t => t.d).i;
                _r = nr;
                break;
            case NavAction.Confirm:
                Press(_rows[_r][_c]);
                break;
            case NavAction.Search: // X: sil
                Backspaced?.Invoke();
                break;
            case NavAction.Options: // Y: boşluk
                Typed?.Invoke(" ");
                break;
            default:
                return false;
        }
        Refresh();
        return true;
    }

    void Press(KeyDef k)
    {
        switch (k.Kind)
        {
            case "char":
                Typed?.Invoke(_shift ? k.Text.ToUpper(Fmt.Tr) : k.Text);
                if (_shift) { _shift = false; Relabel(); }
                break;
            case "shift": _shift = !_shift; Relabel(); break;
            case "space": Typed?.Invoke(" "); break;
            case "back": Backspaced?.Invoke(); break;
            case "done": Done?.Invoke(); break;
        }
        Refresh();
    }

    void Relabel()
    {
        foreach (var k in _rows.SelectMany(r => r).Where(k => k.Kind == "char"))
            k.Label.Text = _shift ? k.Text.ToUpper(Fmt.Tr) : k.Text;
    }

    void Refresh()
    {
        for (int r = 0; r < _rows.Count; r++)
        for (int c = 0; c < _rows[r].Count; c++)
        {
            var k = _rows[r][c];
            bool sel = _focused && r == _r && c == _c;
            bool active = k.Kind == "shift" && _shift;
            k.View.Background = sel ? Brushes.White : active ? new SolidColorBrush(Color.FromArgb(0x55, 255, 255, 255)) : Ui.CardBg;
            k.Label.Foreground = sel ? Ui.Ink : Brushes.White;
        }
    }
}
