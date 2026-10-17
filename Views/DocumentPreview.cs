using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using Nova.Core;
using Nova.Services;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;

namespace Nova.Views;

/// <summary>PDF ve metin dosyalarını Nova içinde önizler.</summary>
public sealed class DocumentPreview : Grid, IOverlay
{
    readonly string _path;
    readonly bool _pdf;
    readonly ScrollViewer _scroll;
    readonly TextBlock _title, _info;
    readonly Image _page = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center };
    PdfDocument? _doc;
    int _pageIndex, _token;
    double _offset;
    bool _closing;

    public event Action? Closed;
    public event Action<string>? Failed;

    public DocumentPreview(string path)
    {
        _path = path;
        _pdf = MediaTypes.IsPdf(path);
        Background = new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x16));
        var (box, canvas) = Ui.Stage();
        Children.Add(box);

        _title = Ui.Text(Path.GetFileName(path), 30);
        _info = Ui.Text(_pdf ? "PDF" : "Metin", 18, Ui.Dim);
        var head = new StackPanel { Margin = new Thickness(120, 50, 120, 0), VerticalAlignment = VerticalAlignment.Top };
        head.Children.Add(_title);
        head.Children.Add(_info);
        canvas.Children.Add(head);

        _scroll = new ScrollViewer
        {
            Margin = new Thickness(_pdf ? 260 : 120, 150, _pdf ? 260 : 120, 100),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Focusable = false,
        };
        canvas.Children.Add(_scroll);

        Loaded += async (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
            if (_pdf) await OpenPdf(); else await OpenText();
        };
    }

    async Task OpenText()
    {
        try
        {
            var info = new FileInfo(_path);
            string text = await Task.Run(() =>
            {
                using var sr = new StreamReader(_path, detectEncodingFromByteOrderMarks: true);
                var buf = new char[Math.Min(info.Length, 2_000_000)];
                int n = sr.ReadBlock(buf, 0, buf.Length);
                return new string(buf, 0, n);
            });
            _info.Text = $"Metin  ·  {Fmt.Size(info.Length)}" + (info.Length > 2_000_000 ? "  ·  ilk 2 MB gösteriliyor" : "");
            _scroll.Content = new TextBlock
            {
                Text = text,
                FontFamily = new FontFamily("Cascadia Mono, Consolas"),
                FontSize = 19,
                Foreground = new SolidColorBrush(Color.FromRgb(0xDD, 0xE3, 0xF0)),
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 28,
            };
        }
        catch { Failed?.Invoke(_path); }
    }

    async Task OpenPdf()
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(_path);
            _doc = await PdfDocument.LoadFromFileAsync(file);
            _scroll.Content = _page;
            await ShowPage(0);
        }
        catch { Failed?.Invoke(_path); }
    }

    async Task ShowPage(int i)
    {
        if (_doc == null) return;
        _pageIndex = Math.Clamp(i, 0, (int)_doc.PageCount - 1);
        int token = ++_token;
        _info.Text = $"PDF  ·  Sayfa {_pageIndex + 1} / {_doc.PageCount}";
        using var page = _doc.GetPage((uint)_pageIndex);
        using var ras = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(ras, new PdfPageRenderOptions { DestinationWidth = 1800 });
        if (token != _token) return;
        ras.Seek(0);
        var ms = new MemoryStream();
        await ras.AsStreamForRead().CopyToAsync(ms);
        ms.Position = 0;
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad;
        bi.StreamSource = ms;
        bi.EndInit();
        bi.Freeze();
        _page.Source = bi;
        _page.Width = 1400;
        ScrollTo(0);
    }

    void ScrollTo(double y)
    {
        _offset = Math.Clamp(y, 0, Math.Max(0, _scroll.ExtentHeight - _scroll.ViewportHeight));
        _scroll.ScrollToVerticalOffset(_offset);
    }

    public IEnumerable<(NavAction, string)> Hints => _pdf
        ? new[] { (NavAction.Left, "Sayfa"), (NavAction.Up, "Kaydır"), (NavAction.Back, "Kapat") }
        : new[] { (NavAction.Up, "Kaydır"), (NavAction.PageDown, "Sayfa"), (NavAction.Back, "Kapat") };

    public bool Handle(NavAction a)
    {
        switch (a)
        {
            case NavAction.Back:
            case NavAction.Menu:
                Close(); return true;
            case NavAction.Up: ScrollTo(_offset - 140); return true;
            case NavAction.Down: ScrollTo(_offset + 140); return true;
            case NavAction.PageUp: ScrollTo(_offset - 700); return true;
            case NavAction.PageDown: ScrollTo(_offset + 700); return true;
            case NavAction.Left:
            case NavAction.TabPrev:
                if (!_pdf || _pageIndex == 0) return false;
                _ = ShowPage(_pageIndex - 1); return true;
            case NavAction.Right:
            case NavAction.TabNext:
                if (!_pdf || _doc == null || _pageIndex >= _doc.PageCount - 1) return false;
                _ = ShowPage(_pageIndex + 1); return true;
        }
        return false;
    }

    public void Close()
    {
        if (_closing) return;
        _closing = true;
        Ui.FadeOut(this, () => Closed?.Invoke());
    }
}
