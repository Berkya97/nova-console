using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nova.Core;
using Nova.Models;

namespace Nova.Views;

/// <summary>
/// Sanallaştırılmış kart ızgarası. Satırlar sanallaştırılır, böylece binlerce dosyalık
/// klasörlerde de yalnızca görünen kartlar oluşturulur.
/// </summary>
public partial class TileGrid : UserControl, IScreen
{
    public const int Cols = 9;
    const double RowH = 232;

    List<TileItem> _items = new();
    int _sel = -1;
    double _targetOffset;
    ScrollViewer? _sv;

    public event Action<TileItem>? Activated;
    public event Action<TileItem>? OptionsRequested;
    public event Action<TileItem?>? SelectionChanged;

    public static readonly DependencyProperty ScrollOffsetProperty = DependencyProperty.Register(
        nameof(ScrollOffset), typeof(double), typeof(TileGrid),
        new PropertyMetadata(0.0, (d, e) => ((TileGrid)d).Sv?.ScrollToVerticalOffset((double)e.NewValue)));

    public double ScrollOffset
    {
        get => (double)GetValue(ScrollOffsetProperty);
        set => SetValue(ScrollOffsetProperty, value);
    }

    public TileGrid()
    {
        InitializeComponent();
        Rows.PreviewMouseLeftButtonDown += OnClick;
        Rows.PreviewMouseWheel += (_, e) =>
        {
            ScrollTo(_targetOffset - Math.Sign(e.Delta) * RowH, true);
            e.Handled = true;
        };
    }

    public string Header { set => HeaderText.Text = value; }
    public string SubHeader { set => CrumbText.Text = value; }
    public string EmptyMessage { set => EmptyText.Text = value; }
    public IEnumerable<(NavAction, string)>? ExtraHints { get; set; }

    public TileItem? Selected => _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;
    public int SelectedIndex => _sel;
    public IReadOnlyList<TileItem> Items => _items;

    public IEnumerable<(NavAction, string)> Hints
    {
        get
        {
            if (Selected is { } s) yield return (NavAction.Confirm, s.ActionLabel);
            if (ExtraHints != null) foreach (var h in ExtraHints) yield return h;
            if (Selected != null) yield return (NavAction.Options, "Seçenekler");
            yield return (NavAction.TabNext, "Sekme");
            if (ExtraHints?.Any(h => h.Item1 == NavAction.Back) != true) yield return (NavAction.Menu, "Güç");
        }
    }

    ScrollViewer? Sv
    {
        get
        {
            if (_sv == null)
            {
                Rows.ApplyTemplate();
                _sv = Rows.Template.FindName("SV", Rows) as ScrollViewer;
            }
            return _sv;
        }
    }

    public void SetItems(List<TileItem> items, int select = 0)
    {
        foreach (var it in _items) it.IsSelected = false;
        _items = items;
        Rows.ItemsSource = items.Chunk(Cols).ToList();
        CountText.Text = items.Count == 0 ? "" : $"{items.Count} öğe";
        EmptyPanel.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _sel = -1;

        BeginAnimation(ScrollOffsetProperty, null);
        _targetOffset = 0;
        ScrollOffset = 0;
        Sv?.ScrollToVerticalOffset(0);

        // Yeni içerik hafifçe aşağıdan belirsin.
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Rows.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        RowsShift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(24, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });

        if (items.Count > 0)
        {
            int sel = Math.Clamp(select, 0, items.Count - 1);
            // Önce yerleşimin oluşmasını bekle ki seçili satıra kaydırılabilsin.
            Dispatcher.BeginInvoke(() => { if (_items == items) Select(sel, animate: false); },
                System.Windows.Threading.DispatcherPriority.Loaded);
            _sel = sel;
            items[sel].IsSelected = true;
            InfoText.Text = items[sel].Subtitle;
            SelectionChanged?.Invoke(items[sel]);
        }
        else
        {
            InfoText.Text = "";
            SelectionChanged?.Invoke(null);
        }
    }

    public bool Handle(NavAction a)
    {
        if (_items.Count == 0) return false;
        int col = _sel % Cols;
        switch (a)
        {
            case NavAction.Left:
                return col > 0 && MoveTo(_sel - 1);
            case NavAction.Right:
                return col < Cols - 1 && _sel + 1 < _items.Count && MoveTo(_sel + 1);
            case NavAction.Up:
                return _sel - Cols >= 0 && MoveTo(_sel - Cols);
            case NavAction.Down:
                if (_sel / Cols >= (_items.Count - 1) / Cols) return false;
                return MoveTo(Math.Min(_sel + Cols, _items.Count - 1));
            case NavAction.PageUp:
                return MoveTo(Math.Max(col, _sel - Cols * 3));
            case NavAction.PageDown:
                return MoveTo(Math.Min(_items.Count - 1, _sel + Cols * 3));
            case NavAction.Confirm:
                if (Selected is { } s) { Activated?.Invoke(s); return true; }
                return false;
            case NavAction.Options:
                if (Selected is { } o) { OptionsRequested?.Invoke(o); return true; }
                return false;
        }
        return false;
    }

    public void SelectIndex(int i)
    {
        if (i >= 0 && i < _items.Count) Select(i);
    }

    bool MoveTo(int i)
    {
        if (i == _sel || i < 0 || i >= _items.Count) return false;
        Select(i);
        return true;
    }

    void Select(int i, bool animate = true)
    {
        bool changed = i != _sel;
        if (Selected is { } old && changed) old.IsSelected = false;
        _sel = i;
        var item = _items[i];
        item.IsSelected = true;
        InfoText.Text = item.Subtitle;
        EnsureRowVisible(i / Cols, animate);
        if (changed) SelectionChanged?.Invoke(item);
    }

    void EnsureRowVisible(int row, bool animate)
    {
        var sv = Sv;
        if (sv == null) return;
        double view = sv.ViewportHeight > 0 ? sv.ViewportHeight : Rows.ActualHeight;
        if (view <= 0) return;

        double top = row * RowH, bottom = top + RowH;
        double target = _targetOffset;
        if (top < target + 8) target = top - (row == 0 ? 0 : RowH * 0.25);
        else if (bottom > target + view - 24) target = bottom - view + RowH * 0.35;
        ScrollTo(target, animate);
    }

    void ScrollTo(double target, bool animate)
    {
        var sv = Sv;
        if (sv == null) return;
        double rows = Math.Ceiling(_items.Count / (double)Cols);
        double view = sv.ViewportHeight > 0 ? sv.ViewportHeight : Rows.ActualHeight;
        double max = Math.Max(0, rows * RowH - view + 20);
        target = Math.Clamp(target, 0, max);
        if (Math.Abs(target - _targetOffset) < 0.5) return;
        _targetOffset = target;

        if (animate)
        {
            BeginAnimation(ScrollOffsetProperty, new DoubleAnimation(sv.VerticalOffset, target, TimeSpan.FromMilliseconds(280))
            { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            BeginAnimation(ScrollOffsetProperty, null);
            ScrollOffset = target;
            sv.ScrollToVerticalOffset(target);
        }
    }

    void OnClick(object sender, MouseButtonEventArgs e)
    {
        var ctx = (e.OriginalSource as FrameworkElement)?.DataContext ?? (e.OriginalSource as FrameworkContentElement)?.DataContext;
        if (ctx is not TileItem t) return;
        int i = _items.IndexOf(t);
        if (i < 0) return;
        if (i == _sel) { Sfx.Confirm(); Activated?.Invoke(t); }
        else { Sfx.Move(); Select(i); }
        e.Handled = true;
    }
}
