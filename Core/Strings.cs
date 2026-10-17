using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;

namespace Nova.Core;

/// <summary>
/// Dil desteği. Arayüz metinleri Türkçe yazılır; İngilizce seçildiğinde her TextBlock'un metni
/// ekrana gelirken (ve sonradan değiştiğinde) bu tablodan çevrilir. Böylece kodun her yerine
/// çeviri çağrısı serpiştirmek gerekmez. Bağlamalar (Binding) SetCurrentValue sayesinde korunur.
/// </summary>
public static partial class L
{
    public static bool En { get; private set; }

    static readonly CultureInfo TrCulture = CultureInfo.GetCultureInfo("tr-TR");
    static readonly CultureInfo EnCulture = CultureInfo.GetCultureInfo("en-US");
    public static CultureInfo Culture => En ? EnCulture : TrCulture;

    public static void Init(string? language)
    {
        En = language == "en";
        if (!En) return;
        // Loaded olayı yalnızca kendi işleyicisi olan öğelere yayınlanır; SizeChanged ise yerleşimde her öğe için
        // tetiklenir. Metin değişince boyut da değiştiğinden sonraki güncellemeler de yakalanır.
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.SizeChangedEvent,
            new SizeChangedEventHandler((s, _) => Hook((TextBlock)s)));
        foreach (var (k, v) in Exact) Upper.TryAdd(k.ToUpper(TrCulture), v.ToUpper(EnCulture));
    }

    static readonly System.ComponentModel.DependencyPropertyDescriptor TextDescriptor =
        System.ComponentModel.DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
    static readonly HashSet<TextBlock> Hooked = new();
    static readonly Dictionary<string, string> Upper = new();
    static bool _busy;

    /// <summary>
    /// İlk yerleşimde metni çevirir ve sonraki metin değişikliklerini izler. Öğe görsel ağaçtan
    /// çıkınca izleme bırakılır (sızıntı olmaz), geri gelirse yeniden bağlanır.
    /// </summary>
    static void Hook(TextBlock tb)
    {
        Apply(tb);
        if (!Hooked.Add(tb)) return;
        TextDescriptor.AddValueChanged(tb, OnTextChanged);
        tb.Unloaded += OnUnloaded;
        tb.Loaded += OnLoaded;
    }

    static void OnTextChanged(object? sender, EventArgs e)
    {
        if (sender is TextBlock tb) Apply(tb);
    }

    static void OnUnloaded(object sender, RoutedEventArgs e)
    {
        var tb = (TextBlock)sender;
        if (!Hooked.Remove(tb)) return;
        TextDescriptor.RemoveValueChanged(tb, OnTextChanged);
    }

    static void OnLoaded(object sender, RoutedEventArgs e)
    {
        var tb = (TextBlock)sender;
        if (!Hooked.Add(tb)) return;
        TextDescriptor.AddValueChanged(tb, OnTextChanged);
        Apply(tb);
    }

    static void Apply(TextBlock tb)
    {
        var text = tb.Text;
        if (_busy || string.IsNullOrEmpty(text)) return;
        var translated = T(text);
        if (translated == text) return;
        _busy = true;
        try { tb.SetCurrentValue(TextBlock.TextProperty, translated); }
        finally { _busy = false; }
    }

    /// <summary>Türkçe metni seçili dile çevirir (Türkçe seçiliyse aynen döner).</summary>
    public static string T(string text)
    {
        if (!En || string.IsNullOrEmpty(text)) return text;
        if (Exact.TryGetValue(text, out var hit)) return hit;
        if (Upper.TryGetValue(text, out var upper)) return upper; // ayarlardaki büyük harfli bölüm başlıkları

        // Çok satırlı yardım metinleri satır satır çevrilir.
        if (text.Contains('\n')) return string.Join("\n", text.Split('\n').Select(T));

        // "Steam  ·  12 saat oynandı  ·  Son oynama: 2 gün önce" gibi birleşik satırlar parça parça çevrilir.
        foreach (var sep in new[] { "   ·   ", "  ·  ", "   ›   ", " · " })
        {
            if (!text.Contains(sep)) continue;
            return string.Join(sep, text.Split(sep).Select(T));
        }

        foreach (var (pattern, replacement) in Patterns)
        {
            var m = pattern.Match(text);
            if (!m.Success) continue;
            var groups = m.Groups.Cast<Group>().Skip(1).Select(g => (object)T(g.Value)).ToArray();
            return string.Format(EnCulture, replacement, groups);
        }

        // Birden fazla cümleden oluşan metinler cümle cümle çevrilir.
        var sentences = SentenceSplit.Split(text);
        if (sentences.Length > 1) return string.Join(" ", sentences.Select(T));
        return text;
    }

    static readonly Regex SentenceSplit = new(@"(?<=[.!?;])\s+(?=[A-ZÇĞİÖŞÜ""])", RegexOptions.Compiled);

    static (Regex, string) P(string pattern, string replacement) =>
        (new Regex("^" + pattern + "$", RegexOptions.Compiled | RegexOptions.Singleline), replacement);
}
