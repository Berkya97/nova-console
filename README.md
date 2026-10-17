# Nova

**Windows için konsol tarzı, tam ekran bir ana ekran.** Oyunlarına, uygulamalarına, medyana ve
dosyalarına koltuktan oyun koluyla ulaş; klavye, fare ve telefonun da her yerde çalışır.
*Türkçe ve İngilizce.*

![Oyunlar](docs/screenshots/oyunlar.png)

## İndir

[**Son sürümü indir**](../../releases/latest) → zip'i aç, `Nova.exe`'yi çalıştır. İstersen
**Ayarlar › Nova'yı bu bilgisayara kur** ile Başlat menüsüne ve masaüstüne kısayol eklenir;
sonraki sürümleri Nova kendisi indirip kurar.

- Windows 10 (1809 ve üzeri) veya Windows 11, 64 bit
- Sürüm sayfasında iki seçenek var: .NET kurulu olmayan bilgisayarlar için tek başına çalışan exe ve
  .NET 9 Masaüstü Çalışma Zamanı kurulu bilgisayarlar için daha küçük exe.

## Özellikler

| | |
|---|---|
| ![Uygulamalar](docs/screenshots/uygulamalar.png) | ![Dosyalar](docs/screenshots/dosyalar.png) |
| ![Arama](docs/screenshots/arama.png) | ![Kontrol Merkezi](docs/screenshots/kontrol-merkezi.png) |
| ![TV ve Video](docs/screenshots/tv.png) | ![Ayarlar](docs/screenshots/ayarlar.png) |

**Kütüphane ve medya**
- **Oyunlar:** Steam, Epic Games, GOG, Xbox / Game Pass, Ubisoft Connect, EA app ve Battle.net oyunlarını kendiliğinden bulur; elle oyun da eklenebilir. Son oynanan başta durur. Steam oyunlarında kapak, tam ekran arka plan, logo, oynama süresi, **son haberler** ve **indirme / güncelleme ilerlemesi** görünür.
- **Uygulamalar:** Masaüstü programları ve Microsoft Store uygulamaları.
- **TV ve Video:** YouTube, Netflix, Disney+, Prime Video, HBO Max, Exxen, tabii, Twitch, Spotify ve kendi eklediğin siteler tam ekran uygulama modunda açılır.
- **Medya:** Fotoğraf görüntüleyici, video ve müzik oynatıcı, PDF ve metin önizleyici; ekran görüntüleri ve klipler için ayrı klasörler.
- **Dosya gezgini:** Küçük resimli ızgara; kopyala / kes / yapıştır, yeniden adlandır, geri dönüşüm kutusu, yeni klasör, sıralama; USB bellek bildirimi.

**Konsol deneyimi**
- **Arka plan modu:** Kapatınca Nova saatin yanındaki simge alanında bekler; kol ya da kısayolla anında açılır. Windows açılışında görünmeden başlayabilir.
- **Kapalıyken de aç:** Nova kapatıldıktan sonra arka planda yaklaşık 9 MB'lık penceresiz bir dinleyici kalır; Ctrl+Alt+N ya da kol kombinasyonuyla Nova yeniden açılır. Simge alanından \"Tamamen kapat\" denince o da kapanır.
- **Oyun içi hızlı menü:** Oyundayken kolda **Start + View** (ya da Ctrl+Alt+G) ile oyunun üstünde panel açılır: Nova'ya dön, ekran görüntüsü, son 30 saniyeyi kaydet (Xbox Game Bar), ses, oyunu kapat; oturum ve günlük oyun süresi görünür.
- **Ekran görüntüsü:** Ctrl+Alt+S ile her yerde; Resimler › Nova klasörüne kaydedilir.
- **Kupalar:** Nova'nın kendi kupaları (bronz, gümüş, altın, platin) ve isteğe bağlı Steam başarımları.
- **Kontrol Merkezi:** Çalan müzik ve kontrolleri, ses, parlaklık, ağ, pil, işlemci / bellek / ekran kartı kullanımı ve sıcaklığı, takvim, hava durumu, uyku zamanlayıcısı, telefonla kumanda, bildirimler.
- **Arama:** Oyun, uygulama ve tüm dosyalarda; oyun koluyla yazmak için ekran klavyesi.
- **Hava durumu** üst çubukta (Open-Meteo; konum otomatik ya da şehir adıyla).

**Kullanım kolaylığı**
- **Telefonla kumanda:** Aynı Wi-Fi'deki telefonun tarayıcısından yön tuşları, A/B/X/Y ve hızlı yazma; PIN korumalı.
- **Birden fazla kullanıcı:** "Kim oynuyor?" ekranı; herkesin sabitledikleri, oynama süreleri, kupaları ve ayarları ayrı.
- **Günlük oyun süresi sınırı** ve **uyku zamanlayıcısı** (uyku ya da kapatma).
- **Kişiselleştirme:** Renk temaları, duvar kağıdı, arka plan müziği, ana ekrana sabitleme.
- **Kurulum ve otomatik güncelleme**, **Türkçe / İngilizce** arayüz.


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
| Sekme değiştir | Q / E, Tab, 1–5 | LB / RB | L1 / R1 |
| Sayfa atla | PgUp / PgDn | LT / RT | L2 / R2 |
| **Nova'ya dön / Nova'yı aç** (kapalıyken de) | **Ctrl + Alt + N** | **Start + View** | **Options + Create** |
| **Oyun içi menü** | **Ctrl + Alt + G** | **Start + View** (oyundayken) | **Options + Create** |
| **Ekran görüntüsü** | **Ctrl + Alt + S** | Oyun içi menüden | Oyun içi menüden |
| Pencere / tam ekran | F11 | — | — |

Xbox ve XInput uyumlu kollar XInput ile, DualShock 4 ve DualSense ise Windows.Gaming.Input ile ek
program gerekmeden desteklenir.

Oyun içi menü pencereli ya da kenarlıksız tam ekran oyunlarda görünür; özel tam ekran modundaki
bazı oyunlarda görünmeyebilir. "Son 30 saniyeyi kaydet" için Xbox Game Bar'da arka plan kaydı açık olmalı.

## Kaynaktan derleme

[.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) gerekir.

```bash
dotnet run --project Nova.csproj
```

Tek dosyalık exe:

```bash
dotnet publish Nova.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o publish-standalone
```

Uygulama simgesi `tools/make-icon.ps1` ile üretilir.

Ayarlar `%AppData%\Nova\settings.json` (ek kullanıcılar için `settings_<id>.json`), kullanıcı listesi
`profiles.json`, indirilen görseller `art\`, hata kayıtları `error.log` içinde tutulur.

### Teknik notlar

- C# / .NET 9, WPF; arayüz 1920x1080 tuval üzerinde tasarlanıp her çözünürlüğe ölçeklenir
- Küçük resimler ve simgeler: Windows Shell `IShellItemImageFactory` (COM)
- Ses seviyesi: Core Audio `IAudioEndpointVolume`; parlaklık ve ekran kartı kullanımı: WMI; NVIDIA'da `nvidia-smi`
- Oyun kolu: XInput ve `Windows.Gaming.Input.RawGameController`
- Dosya araması: Windows Search dizini (OLE DB)
- Çalan medya, ağ bilgisi ve PDF önizleme: WinRT API'leri
- Telefonla kumanda: yerel ağda küçük bir HTTP sunucusu (bağlantı noktası 8642, PIN ve çerezle korunur)
- Dil desteği: arayüz metinleri ekrana gelirken çeviri tablosundan çevrilir
- Arayüz sesleri dosyadan değil, açılışta sentezlenen dalga formlarından üretilir

### Test modu

`NOVA_SCRIPT` ortam değişkeni ayarlanırsa uygulama verilen eylem dizisini kendisi oynatır ve
`NOVA_SHOTS` klasörüne ekran görüntüsü kaydeder:

```
NOVA_SCRIPT="wait:3500 Search type:steam wait:2000 shot:arama quit"
```

## Lisans

[MIT](LICENSE)
