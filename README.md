# Nova

**Windows için konsol tarzı, tam ekran bir ana ekran.** Oyunlarına, uygulamalarına, medyana ve
dosyalarına koltuktan oyun koluyla ulaş; klavye ve fare de her yerde çalışır.

![Oyunlar](docs/screenshots/oyunlar.png)

## İndir

[**Son sürümü indir**](../../releases/latest) → `Nova.exe` dosyasını çalıştır. Kurulum gerekmez.

- Windows 10 (1809 ve üzeri) veya Windows 11, 64 bit
- Sürüm sayfasında iki seçenek var: .NET kurulu olmayan bilgisayarlar için tek başına çalışan exe ve
  .NET 9 Masaüstü Çalışma Zamanı kurulu bilgisayarlar için daha küçük exe.

## Özellikler

| | |
|---|---|
| ![Uygulamalar](docs/screenshots/uygulamalar.png) | ![Dosyalar](docs/screenshots/dosyalar.png) |
| ![Arama](docs/screenshots/arama.png) | ![Kontrol Merkezi](docs/screenshots/kontrol-merkezi.png) |

- **Oyun kütüphanesi:** Steam, Epic Games, GOG, Xbox / Game Pass, Ubisoft Connect, EA app ve Battle.net oyunlarını kendiliğinden bulur, elle oyun eklemeye de izin verir. Son oynanan başta durur. Steam oyunlarında kapak, tam ekran arka plan, logo ve oynama süresi görünür.
- **Uygulamalar:** Masaüstü programları ve Microsoft Store uygulamaları, simgeleriyle.
- **Medya:** Fotoğraf görüntüleyici, video ve müzik oynatıcı, PDF ve metin önizleyici.
- **Dosya gezgini:** Küçük resimli ızgara; kopyala / kes / yapıştır, yeniden adlandır, geri dönüşüm kutusuna taşı, yeni klasör, sıralama. USB bellek takılınca bildirim gelir.
- **Arama:** Oyun, uygulama ve (Windows Arama dizini üzerinden) tüm dosyalarda arar; oyun koluyla yazmak için Türkçe ekran klavyesi var.
- **Kontrol Merkezi:** Çalan müziği (Spotify, tarayıcı vb.) yönetme, ses, parlaklık, ağ, pil, bildirimler.
- **Ana ekrana sabitleme:** Herhangi bir oyun, uygulama, klasör ya da dosya.
- **Oynama süresi:** Steam süreleri Steam'den okunur; diğer oyunlarda süreyi Nova ölçer.
- **Kapak resimleri:** İsteğe bağlı bir [SteamGridDB](https://www.steamgriddb.com/profile/preferences/api) API anahtarıyla Steam dışındaki oyunlara kapak, arka plan ve logo indirilir.
- **Kişiselleştirme:** Renk temaları, duvar kağıdı, arka plan müziği, Windows açılışında başlatma.
- **Oyundan geri dönüş:** Bir oyundayken bile tek kısayolla Nova'ya dönülür.

![Ayarlar](docs/screenshots/ayarlar.png)

## Kontroller

| Eylem | Klavye | Xbox kolu | PS kolu |
|---|---|---|---|
| Gezin | Ok tuşları | D-pad / sol çubuk | D-pad / sol çubuk |
| Seç / Başlat | Enter, Boşluk | A | Çarpı |
| Geri | Esc, Backspace | B | Daire |
| Seçenekler | O | Y | Üçgen |
| Arama | S, Ctrl+F | X | Kare |
| Kontrol Merkezi | C | View | PS / Create / dokunmatik yüzey |
| Güç menüsü | P | Start | Options |
| Sekme değiştir | Q / E, Tab, 1–4 | LB / RB | L1 / R1 |
| Sayfa atla | PgUp / PgDn | LT / RT | L2 / R2 |
| **Nova'ya dön** (oyundayken bile) | **Ctrl + Alt + N** | **Start + View** | **Options + Create** |
| Pencere / tam ekran | F11 | — | — |

Xbox ve XInput uyumlu kollar XInput ile, DualShock 4 ve DualSense ise Windows.Gaming.Input ile ek
program gerekmeden desteklenir.

## Kaynaktan derleme

[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) gerekir.

```bash
dotnet run --project Nova.csproj
```

Tek dosyalık exe:

```bash
dotnet publish Nova.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish-standalone
```

Ayarlar `%AppData%\Nova\settings.json`, indirilen kapaklar `%AppData%\Nova\art`, hata kayıtları
`%AppData%\Nova\error.log` içinde tutulur.

### Teknik notlar

- C# / .NET 9, WPF; arayüz 1920x1080 tuval üzerinde tasarlanıp her çözünürlüğe ölçeklenir
- Küçük resimler ve simgeler: Windows Shell `IShellItemImageFactory` (COM)
- Ses seviyesi: Core Audio `IAudioEndpointVolume`; parlaklık: WMI
- Oyun kolu: XInput ve `Windows.Gaming.Input.RawGameController`
- Dosya araması: Windows Search dizini (OLE DB)
- Çalan medya, ağ bilgisi ve PDF önizleme: WinRT API'leri
- Arayüz sesleri dosyadan değil, açılışta sentezlenen dalga formlarından üretilir

### Test modu

`NOVA_SCRIPT` ortam değişkeni ayarlanırsa uygulama verilen eylem dizisini kendisi oynatır ve
`NOVA_SHOTS` klasörüne ekran görüntüsü kaydeder:

```
NOVA_SCRIPT="wait:3500 Search type:steam wait:2000 shot:arama quit"
```

## Lisans

[MIT](LICENSE)
