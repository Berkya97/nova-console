using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Nova.Core;

namespace Nova.Views;

/// <summary>Sağdan kayarak açılan seçenek / güç menüsü.</summary>
public partial class OptionsMenu : UserControl, IOverlay
{
    public sealed class Entry : INotifyPropertyChanged
    {
        public Entry(string glyph, string label, Action run) { Glyph = glyph; Label = label; Run = run; }
        public string Glyph { get; }
        public string Label { get; }
        public Action Run { get; }

        bool _sel;
        public bool IsSelected
        {
            get => _sel;
            set { _sel = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected))); }
        }
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    readonly List<Entry> _entries;
    int _sel;
    bool _closing;

    public event Action? Closed;

    public OptionsMenu(string title, IList<Entry> entries)
    {
        InitializeComponent();
        TitleText.Text = title;
        _entries = entries.ToList();
        List.ItemsSource = _entries;
        if (_entries.Count > 0) _entries[0].IsSelected = true;

        List.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Entry en) { Sfx.Confirm(); Choose(en); }
        };
        // İmleç menünün açıldığı yerde duruyorsa seçimi değiştirme; yalnızca gerçek fare hareketine tepki ver.
        Point? lastMouse = null;
        List.MouseMove += (_, e) =>
        {
            var pos = e.GetPosition(this);
            bool moved = lastMouse is { } p && (Math.Abs(p.X - pos.X) > 2 || Math.Abs(p.Y - pos.Y) > 2);
            lastMouse ??= pos;
            if (!moved) return;
            lastMouse = pos;
            if ((e.OriginalSource as FrameworkElement)?.DataContext is Entry en) SetSel(_entries.IndexOf(en));
        };

        Loaded += (_, _) =>
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            Card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)));
            Shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)) { EasingFunction = ease });
        };
    }

    public IEnumerable<(NavAction, string)> Hints => new[] { (NavAction.Confirm, "Seç"), (NavAction.Back, "Kapat") };

    public bool Handle(NavAction a)
    {
        switch (a)
        {
            case NavAction.Up: return SetSel(_sel - 1);
            case NavAction.Down: return SetSel(_sel + 1);
            case NavAction.Confirm:
                if (_entries.Count > 0) Choose(_entries[_sel]);
                return true;
            case NavAction.Back:
            case NavAction.Options:
            case NavAction.Menu:
                Close(null);
                return true;
        }
        return false;
    }

    bool SetSel(int i)
    {
        if (i < 0 || i >= _entries.Count || i == _sel) return false;
        _entries[_sel].IsSelected = false;
        _sel = i;
        _entries[i].IsSelected = true;
        return true;
    }

    void Choose(Entry e) => Close(e.Run);

    public void Close(Action? then)
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(140));
        fade.Completed += (_, _) =>
        {
            Closed?.Invoke();
            then?.Invoke();
        };
        Card.BeginAnimation(OpacityProperty, fade);
        Shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(40, TimeSpan.FromMilliseconds(140)));
    }
}
