using Nova.Core;
using Nova.Models;
using Nova.Services;

namespace Nova.Views;

/// <summary>Dosyalar sekmesi: TileGrid üzerinde klasör gezinme ve geri geçmişi.</summary>
public sealed class FileBrowser : IScreen
{
    readonly Stack<(string? Path, int Sel)> _history = new();
    string? _path;
    int _loadToken;

    public TileGrid Grid { get; } = new();

    public event Action<TileItem>? OpenFile;
    public event Action<string>? Error;
    /// <summary>Boş bir klasörde Seçenekler'e basıldı (yapıştır / yeni klasör menüsü için).</summary>
    public event Action? FolderOptions;

    public FileBrowser()
    {
        Grid.Activated += Activate;
        Grid.EmptyMessage = "Bu klasör boş";
    }

    public TileItem? Selected => Grid.Selected;
    public string? CurrentPath => _path;
    public bool CanGoBack => _history.Count > 0;

    public IEnumerable<(NavAction, string)> Hints
    {
        get
        {
            Grid.ExtraHints = CanGoBack ? new[] { (NavAction.Back, "Geri") } : null;
            foreach (var h in Grid.Hints) yield return h;
            if (Grid.Selected == null && _path != null) yield return (NavAction.Options, "Seçenekler");
        }
    }

    public void ShowRoot()
    {
        _history.Clear();
        _ = Load(null, 0);
    }

    /// <summary>Geçerli klasörü yeniden yükler; seçimi mümkünse korur.</summary>
    public void Refresh(string? selectPath = null)
    {
        var keep = selectPath ?? Grid.Selected?.Path;
        _ = Load(_path, Grid.SelectedIndex, keep);
    }

    public async void Navigate(string path)
    {
        _history.Push((_path, Grid.SelectedIndex));
        if (!await Load(path, 0)) _history.Pop();
    }

    /// <summary>Başka bir sekmeden doğrudan bir klasöre atlarken: geri tuşu kök görünüme döner.</summary>
    public async void NavigateFresh(string path)
    {
        _history.Clear();
        _history.Push((null, 0));
        if (!await Load(path, 0)) _history.Clear();
    }

    public void Activate(TileItem t)
    {
        if (t.Kind is TileKind.Folder or TileKind.Drive && t.Path != null) Navigate(t.Path);
        else OpenFile?.Invoke(t);
    }

    public bool Handle(NavAction a)
    {
        if (a == NavAction.Back)
        {
            if (_history.Count == 0) return false;
            var (p, s) = _history.Pop();
            _ = Load(p, s);
            return true;
        }
        if (a == NavAction.Options && Grid.Selected == null && _path != null)
        {
            FolderOptions?.Invoke();
            return true;
        }
        return Grid.Handle(a);
    }

    async Task<bool> Load(string? path, int select, string? selectPath = null)
    {
        int token = ++_loadToken;
        var settings = AppSettings.Current;
        List<TileItem> items;
        try
        {
            items = await Task.Run(() => path == null
                ? FileSystemService.RootItems()
                : FileSystemService.List(path, settings.ShowHidden, settings.FileSort));
        }
        catch (UnauthorizedAccessException)
        {
            Error?.Invoke("Bu klasöre erişim izni yok");
            return false;
        }
        catch (Exception ex)
        {
            Error?.Invoke(ex is DirectoryNotFoundException ? "Klasör bulunamadı" : ex.Message);
            return false;
        }
        if (token != _loadToken) return true;

        if (selectPath != null)
        {
            int i = items.FindIndex(t => string.Equals(t.Path, selectPath, StringComparison.OrdinalIgnoreCase));
            if (i >= 0) select = i;
        }
        foreach (var it in Grid.Items) it.Abandoned = true;
        _path = path;
        Grid.Header = path == null ? "Bu Bilgisayar" : FileSystemService.DisplayName(path);
        Grid.SubHeader = FileSystemService.Breadcrumb(path) + (path != null && settings.FileSort != "Ad" ? $"   ·   Sıralama: {settings.FileSort}" : "");
        Grid.SetItems(items, select);
        return true;
    }
}
