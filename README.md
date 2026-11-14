# HomeworkGuard

**HomeworkGuard**, Windows bilgisayarlarda çocukların günlük ödevlerini tamamlamadan eğlence/oyun erişimine geçmesini engelleyen; **Google Gemini (Multimodal + Structured JSON)** tabanlı iki aşamalı kopya/sahtecilik önleyici ödev doğrulama, **whitelist tabanlı eğitim tarayıcısı**, **haftalık zaman planlama**, **NTP saat manipülasyonu koruması** ve **Telegram uzaktan kontrol** özelliklerine sahip bir Windows Kiosk & Ebeveyn Kontrolü sistemidir.

> [!CAUTION]
> **SORUMLULUK REDDİ BEYANI (DISCLAIMER):**  
> Bu yazılım `ProductionKiosk=true` ile derlenip kurulduğunda Windows servisi, tam ekran kiosk kilidi, klavye kancası, politika sıkılaştırma (Görev Yöneticisi/Registry/ACL kısıtlamaları) uygular.  
> **Yanlış yapılandırma, yönetici şifresinin unutulması veya sistem çakışmaları nedeniyle bilgisayarın kilitlenmesi durumunda geliştiriciler hiçbir sorumluluk kabul etmez.**  
> Lütfen kurulumdan önce [Sorumluluk Reddi Beyanı (DISCLAIMER.md)](DISCLAIMER.md) ve [Kurtarma Kılavuzu](docs/RECOVERY_AND_UNINSTALL.md) belgelerini okuyun ve mümkünse ilk testleri **Sanal Makine (VM) + Snapshot** üzerinde yapın.

---

## Özellikler

### 1. İki Aşamalı Yapay Zeka Ödev Doğrulama (`gemini-3.5-flash-lite`)
- **Aşama 1 — Görsel / İçerik Analizi:** Öğrenci fotoğraf (`JPG`, `PNG`), `PDF`, kaynak kod veya web kamerasından çekilen ödev görüntüsünü yükler. Gemini, yöneticinin belirlediği günlük ödev kriterini (ör. *"En az 10 soru çözülmüş ve işlem adımları açıkça yazılmış olmalıdır"*) kontrol eder. Boş kağıt, yalnızca soru metni, okunaksız görsel veya alakasız ekran görüntüleri reddedilir.
- **Aşama 2 — Sokratik Doğrulama (Anti-Cheat):** Aşama 1'i geçen gönderimlerden sonra yapay zeka, öğrencinin kendi çalışmasındaki belirli soru numaralarına/adımlarına atıfta bulunan **2 adet spesifik doğrulama sorusu** üretir. Öğrenci bu soruları tutarlı şekilde yanıtlamadan bilgisayar kilidi açılmaz.
- **Akıllı Yükleme Koruması:** Gönderim sırasında çift tıklamayı önlemek için butonlar pasifleşir ve cevap dönene kadar **"Ödev kontrol ediliyor..."** durumunu gösterir.

### 2. Eğitim Tarayıcısı (Study Browser)
- Kilit ekranındayken öğrenci yalnızca yöneticinin izin verdiği Türkçe eğitim sitelerine (MEB EBA, Morpa Kampüs, Khan Academy Türkçe, ÖDSGM, Tonguç Akademi vb. varsayılan 12 ders sitesi veya özelleştirilebilir liste) WebView2 tabanlı güvenli tarayıcı üzerinden erişebilir.
- İzin listesi dışındaki tüm alan adları otomatik engellenir.

### 3. Haftalık Gün Bazlı Zamanlama & Kullanım Kotası
- Pazartesi'den Pazar'a her gün için ayrı **Açılış Saati**, **Gece Kilidi Saati**, **Günlük Kullanım Limiti (dk, `0 = sınırsız`)** ve **Günü Tamamen Kapatma** kuralları tanımlanabilir.
- Gece kilidi öncesinde yapılandırılabilir süre kadar uyarı banner'ı gösterilir.
- **NTP Saat Koruması (`TrustedTimeService`):** Öğrencinin Windows saatini geri alarak kilit saatini atlatmasını engellemek için çevrimiçi NTP / HTTP tarih doğrulaması ve monotonic saat farkı kontrolü yapılır.

### 4. Telegram Üzerinden Tam Uzaktan Kontrol ve Canlı Loglama
- Öğrencinin gönderdiği her ödev fotoğrafı/dosyası, Sokratik soru cevapları, puanlar ve kilit açma/kapatma olayları yönetici Telegram sohbetine anlık olarak iletilir.
- Satır içi butonlar (**Inline Keyboard**) ve slash komutları ile uzaktan kilitleme, acil açma, bakım modu, günlük ödev kriteri değiştirme, günlük saat/süre güncelleme ve izinli site yönetimi yapılabilir.
- Ayrıntılar için [Telegram Kurulum Kılavuzu](docs/TELEGRAM_SETUP.md) dosyasına bakın.

### 5. Kiosk Güvenliği, Sistem Tepsisi (Tray) ve Geri Alınabilir Sıkılaştırma
- **Geliştirme Güvenliği (`IS_DEV_SANDBOX_MODE`):** Varsayılan `Debug`/`Release` derlemelerinde `IS_DEV_SANDBOX_MODE = true` olarak kalır; geliştirici makinesinde asla tam ekran kilit veya sistem sıkılaştırması uygulanmaz.
- **Üretim Modu (`ProductionKiosk=true`):** `LocalSystem` yetkili `HomeworkGuardService` watchdog servisi, masaüstü kiosk kilidi, düşük seviyeli klavye kancası ve `Install-Hardening.ps1` ile kurulan ACL/kayıt defteri koruması aktifleşir.
- **Anlık Durum Yedekleme & Geri Yükleme:** Kurulum sırasında değiştirilen sistem kayıt defteri değerlerinin orijinal durumu yedeklenir; **"Korumayı Kaldır"** veya `Uninstall-Hardening.ps1` çalıştırıldığında sistem ayarları eski haline döndürülür.
- **Sistem Tepsisi (System Tray):** Erişim açıldığında veya Yönetici Bakım Moduna geçildiğinde uygulama gizli simgeler alanında çalışmaya devam eder; yönetici tek tıkla korumayı yeniden devreye alabilir veya Yönetici Konsolunu açabilir.

---

## Proje Yapısı

```text
HomeworkGuard/
├── HomeworkGuard.sln
├── Directory.Build.props
├── DISCLAIMER.md                         # Sorumluluk reddi beyanı
├── LICENSE                               # MIT Lisansı
├── scripts/
│   ├── Build-ReleaseForVM.ps1            # VM / üretim için ProductionKiosk=true paketleyici
│   ├── Install-Hardening.ps1             # Servis, ACL ve politika kurulum betiği
│   └── Uninstall-Hardening.ps1           # Ayarları geri yükleyen kaldırma betiği
├── src/
│   ├── HomeworkGuard.Core/               # Modeller, IPC sözleşmeleri, DPAPI/PBKDF2 kripto yardımcıları
│   ├── HomeworkGuard.Service/            # Windows Servisi, Gemini AI, SQLite, Telegram Bot, Watchdog, NTP
│   ├── HomeworkGuard.UI/                 # WPF Kiosk Kilit Ekranı, WebView2 Tarayıcı, Yönetici Konsolu, Tray
│   └── HomeworkGuard.Setup/              # Grafiksel Kurulum ve Kaldırma Sihirbazı (GUI)
├── tests/
│   └── HomeworkGuard.Tests/              # xUnit birim testleri (24 test)
└── docs/
    ├── ARCHITECTURE.md                   # Teknik mimari ve IPC protokolü
    ├── TELEGRAM_SETUP.md                 # Telegram bot kurulumu ve komutlar
    └── RECOVERY_AND_UNINSTALL.md         # Güvenli kaldırma ve acil kurtarma adımları
```

---

## Gereksinimler

- **İşletim Sistemi:** Windows 10 / Windows 11 (x64)
- **SDK:** [.NET 10 SDK](https://dotnet.microsoft.com/download)
- **Çalışma Zamanı:** Microsoft Edge WebView2 Runtime (Windows 11'de yerleşik gelir)
- **API Anahtarları:**
  - Google AI Studio üzerinden **Gemini API Key** (`gemini-3.5-flash-lite` erişimli)
  - Telegram `@BotFather` üzerinden **Bot Token** ve yönetici **Chat ID**

---

## Derleme ve Test

### 1. Birim Testlerini Çalıştırma (Host PC - Güvenli Sandbox Modu)

```powershell
dotnet test HomeworkGuard.sln -c Release
```

> Not: Normal `dotnet build` / `dotnet test` komutlarında `IS_DEV_SANDBOX_MODE = true` aktif olduğu için geliştirme bilgisayarınızda kilit ekranı veya sistem kısıtlaması devreye girmez.

### 2. Sanal Makine / Üretim Paketi Oluşturma (`ProductionKiosk=true`)

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\Build-ReleaseForVM.ps1
```

Bu betik `Publish_VM/` klasörü içerisine `HomeworkGuard.Service`, `HomeworkGuard.UI`, `HomeworkGuard.Setup`, `Install-Hardening.ps1` ve `Uninstall-Hardening.ps1` dosyalarını `win-x64` self-contained olarak paketler.

---

## Kurulum (Önerilen: Sanal Makine Üzerinde)

1. `Publish_VM/` klasörünü hedef Windows makineye (ör. VirtualBox Windows 11 VM masaüstüne) kopyalayın.
2. **`HomeworkGuard.Setup.exe`** uygulamasını yönetici olarak çalıştırın.
3. Kurulum arayüzünden:
   - En az 6 karakterlik **Yönetici Şifresi** belirleyin,
   - **Gemini API Key**, **Telegram Bot Token** ve **Admin Chat ID** bilgilerini girin (alanların yanındaki `?` yardım butonlarından nasıl alınacağını görebilirsiniz),
   - Günlük veya haftalık **Açılış / Gece Kilidi / Süre Limiti** ve **Günlük Ödev Kriteri** ayarlarını yapın.
4. **Kurulumu Başlat** butonuna tıklayın. Kurulum tamamlandığında ekranda tek seferlik **Acil Kurtarma Anahtarı** gösterilir — bu anahtarı mutlaka bilgisayar dışında güvenli bir yere not edin.

---

## Dokümantasyon

- [Teknik Mimari (`docs/ARCHITECTURE.md`)](docs/ARCHITECTURE.md)
- [Telegram Bot Kurulumu ve Komutlar (`docs/TELEGRAM_SETUP.md`)](docs/TELEGRAM_SETUP.md)
- [Kaldırma ve Acil Kurtarma (`docs/RECOVERY_AND_UNINSTALL.md`)](docs/RECOVERY_AND_UNINSTALL.md)
- [Sorumluluk Reddi Beyanı (`DISCLAIMER.md`)](DISCLAIMER.md)

---

## Lisans

Bu proje [MIT Lisansı](LICENSE) altında lisanslanmıştır.
