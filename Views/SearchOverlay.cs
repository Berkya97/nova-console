using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Nova.Core;
using Nova.Models;
using Nova.Services;

namespace Nova.Views;

/// <summary>Oyun, uygulama ve dosya araması: solda ekran klavyesi, sağda sonuçlar.</summary>
public sealed class SearchOverlay : Grid, IOverlay, ITextTarget
{
    readonly Func<IEnumerable<TileItem>> _local;
    readonly TextBlock _text, _placeholder, _status;
    readonly OnScreenKeyboard _osk;
    readonly StackPanel _list;
    readonly ScrollViewer _scroll;
    readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(260) };
    readonly List<(TileItem Item, Border Row)> _results = new();
    string _query = "";
    int _sel;
    bool _inResults, _preferResults, _closing;
    int _token;

    public event Action? Closed;
    public event Action<TileItem>? Activated;

    public SearchOverlay(Func<IEnumerable<TileItem>> local, bool startInKeyboard)
    {
        _local = local;
        Background = Ui.Backdrop;
        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        // Arama kutusu
        _text = Ui.Text("", 32);
        _placeholder = Ui.Text("Oyun, uygulama veya dosya ara", 30, Ui.Faint);
        var caret = new Border { Width = 2, Height = 36, Background = Brushes.White, Margin = new Thickness(3, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        caret.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(550)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var line = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        line.Children.Add(Ui.Glyph(Glyphs.Search, 26, Ui.Dim));
        line.Children.Add(new Border { Width = 22 });
        line.Children.Add(_text);
        line.Children.Add(caret);
        line.Children.Add(new Border { Width = 12 });
        line.Children.Add(_placeholder);
        canvas.Children.Add(new Border
        {
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(120, 110, 120, 0),
            Height = 88,
            CornerRadius = new CornerRadius(44),
            Background = Ui.CardBg,
            BorderBrush = Ui.Line,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(36, 0, 36, 0),
            Child = line,
        });

        // Ekran klavyesi
        _osk = new OnScreenKeyboard { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(120, 250, 0, 0) };
        _osk.Typed += TypeText;
        _osk.Backspaced += Backspace;
        _osk.Done += () => { if (_results.Count > 0) { _inResults = _preferResults = true; Refresh(); } };
        canvas.Children.Add(_osk);

        // Sonuçlar
        _status = Ui.Text("Yazmaya başla", 22, Ui.Faint);
        _status.HorizontalAlignment = HorizontalAlignment.Left;
        _status.VerticalAlignment = VerticalAlignment.Top;
        _status.Margin = new Thickness(1040, 262, 0, 0);
        canvas.Children.Add(_status);

        _list = new StackPanel();
        _scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _list,
            Margin = new Thickness(1020, 250, 100, 100),
            Focusable = false,
        };
        canvas.Children.Add(_scroll);

        _inResults = _preferResults = !startInKeyboard;
        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        Loaded += (_, _) => Ui.FadeIn(box, 20);
        Render();
        Refresh();
    }

    public IEnumerable<(NavAction, string)> Hints => _inResults
        ? new[] { (NavAction.Confirm, "Aç"), (NavAction.Left, "Klavye"), (NavAction.Back, "Kapat") }
        : new[] { (NavAction.Confirm, "Tuşa bas"), (NavAction.Search, "Sil"), (NavAction.Options, "Boşluk"), (NavAction.Back, "Kapat") };

    public bool Handle(NavAction a)
    {
        if (a is NavAction.Back) { Close(); return true; }
        if (_inResults)
        {
            switch (a)
            {
                case NavAction.Up: return Move(-1);
                case NavAction.Down: return Move(1);
                case NavAction.PageUp: return Move(-6);
                case NavAction.PageDown: return Move(6);
                case NavAction.Left:
                    _inResults = _preferResults = false; Refresh(); return true;
                case NavAction.Confirm:
                    ActivateSelected(); return true;
                case NavAction.Search:
                    Backspace(); return true;
            }
            return false;
        }

        if (_osk.Handle(a)) return true;
        if (a == NavAction.Right && _results.Count > 0)
        {
            _inResults = _preferResults = true;
            Refresh();
            return true;
        }
        return false;
    }

    bool Move(int d)
    {
        int n = Math.Clamp(_sel + d, 0, _results.Count - 1);
        if (n == _sel || _results.Count == 0) return false;
        _sel = n;
        Refresh();
        return true;
    }

    public void TypeText(string text) { _query += text; Changed(); }

    public void Backspace()
    {
        if (_query.Length == 0) return;
        _query = _query[..^1];
        Changed();
    }

    public void Submit()
    {
        if (_results.Count > 0) ActivateSelected();
    }

    void Changed()
    {
        Render();
        _debounce.Stop();
        _debounce.Start();
    }

    void Render()
    {
        _text.Text = _query;
        _placeholder.Visibility = _query.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    async void RunSearch()
    {
        int token = ++_token;
        var q = _query.Trim();
        if (q.Length == 0) { Show(new List<TileItem>(), "Yazmaya başla"); return; }

        var local = SearchService.Match(_local(), q, 12);
        Show(local, local.Count == 0 ? "Dosyalarda aranıyor…" : null);

        var files = await Task.Run(() => SearchService.Files(q, 24));
        if (token != _token) return;
        var all = local.Concat(files).ToList();
        Show(all, all.Count == 0 ? $"\"{q}\" için sonuç bulunamadı" : null);
    }

    void Show(List<TileItem> items, string? status)
    {
        var keep = _results.ElementAtOrDefault(_sel).Item;
        _results.Clear();
        _list.Children.Clear();
        foreach (var it in items)
        {
            var row = BuildRow(it);
            _results.Add((it, row));
            _list.Children.Add(row);
        }
        _sel = keep != null ? Math.Max(0, items.IndexOf(keep)) : 0;
        // Fiziksel klavye kullanan biri sonuç listesinde kalmak ister; sonuçlar geç gelse bile oraya dön.
        _inResults = _results.Count > 0 && (_inResults || _preferResults);
        _status.Text = status ?? "";
        _status.Visibility = status == null ? Visibility.Collapsed : Visibility.Visible;
        Refresh();
    }

    Border BuildRow(TileItem it)
    {
        var kind = it.Kind switch
        {
            TileKind.Game => "Oyun" + (it.Source.Length > 0 ? "  ·  " + it.Source : ""),
            TileKind.App => it.Source == "Store" ? "Microsoft Store uygulaması" : "Uygulama",
            TileKind.Folder or TileKind.Drive => it.Subtitle,
            _ => it.Subtitle,
        };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(20, 0, 0, 0) };
        text.Children.Add(Ui.Text(it.Title, 23, weight: FontWeights.SemiBold));
        text.Children.Add(Ui.Text(kind, 16, Ui.Dim));
        var dock = new DockPanel();
        var icon = Ui.TileIcon(it, 62);
        DockPanel.SetDock(icon, Dock.Left);
        dock.Children.Add(icon);
        dock.Children.Add(text);
        var row = new Border
        {
            Height = 86,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12),
            Margin = new Thickness(0, 0, 0, 6),
            BorderThickness = new Thickness(2),
            Child = dock,
            Cursor = Cursors.Hand,
        };
        row.MouseLeftButtonUp += (_, _) =>
        {
            _sel = _results.FindIndex(r => r.Row == row);
            _inResults = true;
            Sfx.Confirm();
            ActivateSelected();
        };
        return row;
    }

    void Refresh()
    {
        _osk.ShowsSelection = !_inResults;
        for (int i = 0; i < _results.Count; i++)
        {
            bool sel = _inResults && i == _sel;
            _results[i].Row.Background = sel ? new SolidColorBrush(Color.FromArgb(0x30, 255, 255, 255)) : Brushes.Transparent;
            Ui.SetRing(_results[i].Row, sel);
        }
        if (_inResults && _sel < _results.Count) _results[_sel].Row.BringIntoView();
    }

    void ActivateSelected()
    {
        if (_sel >= _results.Count) return;
        var item = _results[_sel].Item;
        Close();
        Activated?.Invoke(item);
    }

    void Close()
    {
        if (_closing) return;
        _closing = true;
        _debounce.Stop();
        Ui.FadeOut(this, () => Closed?.Invoke());
    }
}
