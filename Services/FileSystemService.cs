using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media;
using Nova.Core;
using Nova.Models;

namespace Nova.Services;

public static class FileSystemService
{
    [DllImport("shell32.dll")]
    static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    public static string Downloads
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(DownloadsId, 0, IntPtr.Zero, out var p) == 0)
                {
                    var s = Marshal.PtrToStringUni(p)!;
                    Marshal.FreeCoTaskMem(p);
                    return s;
                }
            }
            catch { }
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        }
    }

    public static (string Title, string Path, string Glyph, Color Color)[] QuickFolders() => new[]
    {
        ("Masaüstü", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Glyphs.Desktop, Theme.Blue),
        ("Belgeler", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), Glyphs.Documents, Theme.Violet),
        ("İndirilenler", Downloads, Glyphs.Downloads, Theme.Green),
        ("Resimler", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), Glyphs.Pictures, Theme.Amber),
        ("Videolar", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), Glyphs.Videos, Theme.Red),
        ("Müzik", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), Glyphs.Music, Theme.Pink),
    };

    public static TileItem FolderTile(string title, string path, string glyph, Color color) => new()
    {
        Kind = TileKind.Folder,
        Title = title,
        Path = path,
        InitGlyph = glyph,
        Accent = color,
        TileBrush = Theme.Tile(color),
        Subtitle = "Klasör  ·  " + path,
        ActionLabel = "Göz at",
        ActionGlyph = Glyphs.OpenFolder,
    };

    /// <summary>"Bu Bilgisayar" görünümü: hızlı erişim klasörleri ve sürücüler.</summary>
    public static List<TileItem> RootItems()
    {
        var list = QuickFolders()
            .Where(q => Directory.Exists(q.Path))
            .Select(q => FolderTile(q.Title, q.Path, q.Glyph, q.Color))
            .ToList();

        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady) continue;
                var letter = d.Name.TrimEnd('\\');
                var label = string.IsNullOrWhiteSpace(d.VolumeLabel)
                    ? d.DriveType switch
                    {
                        DriveType.Removable => "USB Sürücü",
                        DriveType.CDRom => "DVD Sürücü",
                        DriveType.Network => "Ağ Sürücüsü",
                        _ => "Yerel Disk",
                    }
                    : d.VolumeLabel;
                double used = d.TotalSize > 0 ? 1 - (double)d.TotalFreeSpace / d.TotalSize : 0;
                list.Add(new TileItem
                {
                    Kind = TileKind.Drive,
                    Title = $"{label} ({letter})",
                    Path = d.RootDirectory.FullName,
                    InitGlyph = d.DriveType switch
                    {
                        DriveType.Removable => Glyphs.Usb,
                        DriveType.CDRom => Glyphs.Disc,
                        DriveType.Network => Glyphs.Network,
                        _ => Glyphs.Drive,
                    },
                    Accent = Theme.Cyan,
                    TileBrush = Theme.Tile(used > 0.9 ? Theme.Red : Theme.Cyan),
                    Caption = $"{Fmt.Size(d.TotalFreeSpace)} boş / {Fmt.Size(d.TotalSize)}",
                    Subtitle = $"{Fmt.Size(d.TotalFreeSpace)} boş  ·  toplam {Fmt.Size(d.TotalSize)}  ·  {d.DriveFormat}",
                    UsageWidth = Math.Max(4, used * 132),
                    ActionLabel = "Göz at",
                    ActionGlyph = Glyphs.OpenFolder,
                });
            }
            catch { }
        }
        return list;
    }

    /// <summary>Bir klasörün içeriği: önce klasörler, sonra dosyalar, doğal sıralamayla.</summary>
    public static readonly string[] SortModes = { "Ad", "Tarih", "Boyut", "Tür" };

    public static List<TileItem> List(string path, bool showHidden = false, string sort = "Ad")
    {
        var di = new DirectoryInfo(path);
        var opts = new EnumerationOptions
        {
            IgnoreInaccessible = false,
            AttributesToSkip = showHidden ? FileAttributes.System : FileAttributes.Hidden | FileAttributes.System,
        };

        var dirs = di.EnumerateDirectories("*", opts).ToList();
        var files = di.EnumerateFiles("*", opts).ToList();
        dirs.Sort((a, b) => sort == "Tarih" ? b.LastWriteTime.CompareTo(a.LastWriteTime) : NaturalComparer.Instance.Compare(a.Name, b.Name));
        files.Sort((a, b) => sort switch
        {
            "Tarih" => b.LastWriteTime.CompareTo(a.LastWriteTime),
            "Boyut" => b.Length.CompareTo(a.Length),
            "Tür" => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase) is var c && c != 0
                ? c : NaturalComparer.Instance.Compare(a.Name, b.Name),
            _ => NaturalComparer.Instance.Compare(a.Name, b.Name),
        });

        var list = new List<TileItem>(dirs.Count + files.Count);
        foreach (var d in dirs)
        {
            list.Add(new TileItem
            {
                Kind = TileKind.Folder,
                Title = d.Name,
                Path = d.FullName,
                InitGlyph = Glyphs.Folder,
                TileBrush = Theme.Translucent,
                Subtitle = "Klasör  ·  " + Fmt.Date(d.LastWriteTime),
                ActionLabel = "Aç",
                ActionGlyph = Glyphs.OpenFolder,
            });
        }
        foreach (var f in files) list.Add(FileTile(f));
        return list;
    }

    public static TileItem FileTile(FileInfo f)
    {
        var kind = MediaTypes.KindOf(f.FullName);
        bool doc = MediaTypes.IsText(f.FullName) || MediaTypes.IsPdf(f.FullName);
        var label = doc ? (MediaTypes.IsPdf(f.FullName) ? "PDF" : "Metin") : MediaTypes.Label(kind);
        return new TileItem
        {
            Kind = TileKind.File,
            Title = f.Name,
            Path = f.FullName,
            TileBrush = Theme.Translucent,
            Subtitle = $"{label}  ·  {Fmt.Size(f.Length)}  ·  {Fmt.Date(f.LastWriteTime)}",
            ActionLabel = kind switch
            {
                MediaKind.Image => "Görüntüle",
                MediaKind.Video => "Oynat",
                MediaKind.Audio => "Dinle",
                _ => doc ? "Önizle" : "Aç",
            },
            ActionGlyph = kind is MediaKind.Video or MediaKind.Audio ? Glyphs.Play : kind == MediaKind.Image ? Glyphs.View : Glyphs.Open,
        };
    }

    public static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('\\'));
        if (string.IsNullOrEmpty(name)) name = path.TrimEnd('\\');
        foreach (var q in QuickFolders())
            if (string.Equals(q.Path.TrimEnd('\\'), path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                return q.Title;
        return name;
    }

    public static string Breadcrumb(string? path)
    {
        if (path == null) return "Dosyalar";
        var parts = path.TrimEnd('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries).ToList();
        parts.RemoveAt(parts.Count - 1);
        if (parts.Count > 4) parts = new[] { "…" }.Concat(parts.Skip(parts.Count - 3)).ToList();
        return string.Join("   ›   ", new[] { "Bu Bilgisayar" }.Concat(parts));
    }

    /// <summary>Medya sekmesi için resim/video/müzik klasörlerindeki en yeni dosyalar.</summary>
    public static List<TileItem> RecentMedia(int count)
    {
        var folders = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            Downloads,
        };
        var opts = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            MaxRecursionDepth = 2,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
        };

        var found = new List<FileInfo>();
        foreach (var dir in folders.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                found.AddRange(new DirectoryInfo(dir).EnumerateFiles("*", opts)
                    .Take(4000)
                    .Where(f => MediaTypes.KindOf(f.FullName) != MediaKind.None));
            }
            catch { }
        }
        return found
            .DistinctBy(f => f.FullName.ToLowerInvariant())
            .OrderByDescending(f => f.LastWriteTime)
            .Take(count)
            .Select(FileTile)
            .ToList();
    }
}
