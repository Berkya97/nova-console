using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nova.Core;
using Nova.Models;

namespace Nova.Views;

/// <summary>Konsol ana ekranı tarzı yatay satır: seçilen kart sola sabitlenir, satır kayar.</summary>
public partial class HomeRowView : UserControl, IScreen
{
    const double Step = 120; // kart (104) + boşluk (16)

    List<TileItem> _items = new();
    int _sel = -1;
    int _detailToken;

    public event Action<TileItem>? Activated;
    public event Action<TileItem>? OptionsRequested;
    public event Action<TileItem?>? SelectionChanged;

    public string CountFormat { get; set; } = "{0} öğe";

    public HomeRowView()
    {
        InitializeComponent();
        Row.PreviewMouseLeftButtonDown += OnRowClick;
        MouseWheel += (_, e) => { if (Move(e.Delta > 0 ? -1 : 1)) Sfx.Move(); };
        PrimaryBtn.MouseLeftButtonUp += (_, _) => { if (Selected is { } s) { Sfx.Confirm(); Activated?.Invoke(s); } };
        MoreBtn.MouseLeftButtonUp += (_, _) => { if (Selected is { } s) OptionsRequested?.Invoke(s); };
    }

    public TileItem? Selected => _sel >= 0 && _sel < _items.Count ? _items[_sel] : null;
    public IReadOnlyList<TileItem> Items => _items;

    public IEnumerable<(NavAction, string)> Hints
    {
        get
        {
            if (Selected is { } s) yield return (NavAction.Confirm, s.ActionLabel);
            if (Selected is { Kind: not TileKind.Action }) yield return (NavAction.Options, "Seçenekler");
            yield return (NavAction.TabNext, "Sekme");
            yield return (NavAction.Menu, "Güç");
        }
    }

    public void SetItems(IList<TileItem> items, int select = 0)
    {
        foreach (var it in _items) it.IsSelected = false;
        _items = items.ToList();
        // Küçük resim kuyruğu LIFO çalışır; tersten isteyince soldaki kartlar önce yüklenir.
        for (int i = _items.Count - 1; i >= 0; i--) _items[i].EnsureThumb();
        Row.ItemsSource = _items;
        _sel = -1;
        CountText.Text = _items.Count > 1 ? string.Format(CountFormat, _items.Count) : "";
        if (_items.Count > 0) Select(Math.Clamp(select, 0, _items.Count - 1), animate: false);
        else { UpdateDetail(null); SelectionChanged?.Invoke(null); }
    }

    public void SelectItem(TileItem item)
    {
        int i = _items.IndexOf(item);
        if (i >= 0) Select(i);
    }

    public bool Handle(NavAction a)
    {
        switch (a)
        {
            case NavAction.Left: return Move(-1);
            case NavAction.Right: return Move(1);
            case NavAction.PageUp: return Move(-6);
            case NavAction.PageDown: return Move(6);
            case NavAction.Confirm:
                if (Selected is { } s) { Activated?.Invoke(s); return true; }
                return false;
            case NavAction.Options:
                if (Selected is { Kind: not TileKind.Action } o) { OptionsRequested?.Invoke(o); return true; }
                return false;
        }
        return false;
    }

    bool Move(int delta)
    {
        if (_items.Count == 0) return false;
        int n = Math.Clamp(_sel + delta, 0, _items.Count - 1);
        if (n == _sel) return false;
        Select(n);
        return true;
    }

    void Select(int i, bool animate = true)
    {
        if (Selected is { } old)
        {
            old.IsSelected = false;
            old.PropertyChanged -= OnSelectedChanged;
        }
        _sel = i;
        var item = _items[i];
        item.IsSelected = true;
        item.PropertyChanged += OnSelectedChanged;

        double to = -i * Step;
        if (animate)
        {
            RowShift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(to, TimeSpan.FromMilliseconds(340)) { EasingFunction = new QuinticEase { EasingMode = EasingMode.EaseOut } });
        }
        else
        {
            RowShift.BeginAnimation(TranslateTransform.XProperty, null);
            RowShift.X = to;
        }

        UpdateDetail(item);
        SelectionChanged?.Invoke(item);
    }

    async void UpdateDetail(TileItem? item)
    {
        int token = ++_detailToken;
        if (item == null) { Detail.Visibility = Visibility.Hidden; return; }

        Detail.Visibility = Visibility.Visible;
        BigTitle.Text = item.Title;
        BigTitle.Visibility = Visibility.Visible;
        Logo.Visibility = Visibility.Collapsed;
        Logo.Source = null;
        Meta.Text = item.Subtitle;
        ActionText.Text = item.ActionLabel;
        ActionGlyph.Text = item.ActionGlyph;
        MoreBtn.Visibility = item.Kind == TileKind.Action ? Visibility.Collapsed : Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        Detail.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
        DetailShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(28, 0, TimeSpan.FromMilliseconds(380)) { EasingFunction = ease });

        if (item.LogoPath != null)
        {
            var logo = await ImageCache.LoadAsync(item.LogoPath, 800);
            if (token != _detailToken || logo == null) return;
            Logo.Source = logo;
            Logo.Visibility = Visibility.Visible;
            BigTitle.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>Kapak/logo sonradan indirilirse (SteamGridDB) detay alanını yenile.</summary>
    void OnSelectedChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TileItem.LogoPath) && sender == Selected) UpdateDetail(Selected);
    }

    void OnRowClick(object sender, MouseButtonEventArgs e)
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
