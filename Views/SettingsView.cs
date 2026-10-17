using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Nova.Core;

namespace Nova.Views;

/// <summary>Ayarlar listesinde bir satır. Değer sol/sağ ile değişir, Onay ile eylem çalışır.</summary>
public sealed class SettingRow
{
    public string Glyph { get; init; } = Glyphs.Settings;
    public string Label { get; init; } = "";
    public string Help { get; init; } = "";
    public Func<string> Value { get; init; } = () => "";
    public Action<int>? Change { get; init; }
    public Action? Activate { get; init; }
    public string? Section { get; init; }

    internal Border View = null!;
    internal TextBlock ValueText = null!;
}

public sealed class SettingsView : Grid, IOverlay
{
    readonly List<SettingRow> _rows;
    readonly TextBlock _help;
    readonly ScrollViewer _scroll;
    int _sel;
    bool _closing;

    public event Action? Closed;

    public SettingsView(List<SettingRow> rows)
    {
        _rows = rows;
        // Tema değişikliği arkada görünsün diye biraz şeffaf.
        Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x05, 0x08, 0x12));
        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        var title = Ui.Text("Ayarlar", 54, weight: FontWeights.Light);
        title.VerticalAlignment = VerticalAlignment.Top;
        title.Margin = new Thickness(120, 100, 0, 0);
        canvas.Children.Add(title);

        var list = new StackPanel();
        foreach (var r in rows)
        {
            if (r.Section != null)
            {
                var s = Ui.Text(r.Section.ToUpper(Fmt.Tr), 15, Ui.Faint, FontWeights.SemiBold);
                s.Margin = new Thickness(24, list.Children.Count == 0 ? 0 : 26, 0, 10);
                list.Children.Add(s);
            }
            r.ValueText = Ui.Text("", 20, Ui.Dim);
            var dock = new DockPanel();
            var g = Ui.Glyph(r.Glyph, 22);
            g.Width = 34;
            dock.Children.Add(g);
            DockPanel.SetDock(r.ValueText, Dock.Right);
            dock.Children.Insert(0, r.ValueText);
            var label = Ui.Text(r.Label, 22);
            label.Margin = new Thickness(18, 0, 0, 2);
            dock.Children.Add(label);
            r.View = new Border
            {
                Height = 68,
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(24, 0, 28, 0),
                Margin = new Thickness(0, 0, 0, 4),
                BorderThickness = new Thickness(2),
                Child = dock,
                Cursor = Cursors.Hand,
            };
            var row = r;
            r.View.MouseLeftButtonUp += (_, _) => { _sel = _rows.IndexOf(row); Sfx.Confirm(); Run(row, +1); };
            list.Children.Add(r.View);
        }
        _scroll = new ScrollViewer
        {
            Content = list,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Margin = new Thickness(100, 200, 820, 80),
            Focusable = false,
        };
        canvas.Children.Add(_scroll);

        _help = Ui.Text("", 22, Ui.Dim);
        _help.TextWrapping = TextWrapping.Wrap;
        _help.TextTrimming = TextTrimming.None;
        _help.VerticalAlignment = VerticalAlignment.Top;
        _help.Margin = new Thickness(1200, 220, 140, 0);
        canvas.Children.Add(_help);

        var version = Ui.Text("Nova " + typeof(SettingsView).Assembly.GetName().Version?.ToString(3), 18, Ui.Faint);
        version.VerticalAlignment = VerticalAlignment.Bottom;
        version.HorizontalAlignment = HorizontalAlignment.Right;
        version.Margin = new Thickness(0, 0, 140, 110);
        canvas.Children.Add(version);

        Loaded += (_, _) => { Ui.FadeIn(box, 20); Refresh(); };
    }

    public IEnumerable<(NavAction, string)> Hints
    {
        get
        {
            var r = _rows[_sel];
            if (r.Change != null) yield return (NavAction.Left, "Değiştir");
            if (r.Activate != null || r.Change != null) yield return (NavAction.Confirm, r.Activate != null ? "Seç" : "Değiştir");
            yield return (NavAction.Back, "Kapat");
        }
    }

    public bool Handle(NavAction a)
    {
        switch (a)
        {
            case NavAction.Up:
                if (_sel == 0) return false;
                _sel--; Refresh(); return true;
            case NavAction.Down:
                if (_sel == _rows.Count - 1) return false;
                _sel++; Refresh(); return true;
            case NavAction.Left:
            case NavAction.Right:
                if (_rows[_sel].Change is not { } change) return false;
                change(a == NavAction.Right ? 1 : -1);
                Refresh();
                return true;
            case NavAction.Confirm:
                return Run(_rows[_sel], +1);
            case NavAction.Back:
            case NavAction.Menu:
                Close();
                return true;
        }
        return false;
    }

    bool Run(SettingRow r, int dir)
    {
        if (r.Activate != null) r.Activate();
        else if (r.Change != null) r.Change(dir);
        else return false;
        Refresh();
        return true;
    }

    /// <summary>Değerleri yeniden okur (dışarıda değişen ayarlar için de çağrılır).</summary>
    public void Refresh()
    {
        for (int i = 0; i < _rows.Count; i++)
        {
            var r = _rows[i];
            bool sel = i == _sel;
            r.ValueText.Text = r.Value();
            r.View.Background = sel ? new SolidColorBrush(Color.FromArgb(0x2E, 255, 255, 255)) : Brushes.Transparent;
            Ui.SetRing(r.View, sel);
        }
        _help.Text = _rows[_sel].Help;
        _rows[_sel].View.BringIntoView();
    }

    void Close()
    {
        if (_closing) return;
        _closing = true;
        Ui.FadeOut(this, () => Closed?.Invoke());
    }
}
