# Kaldırma ve Acil Kurtarma Kılavuzu (`RECOVERY_AND_UNINSTALL.md`)

> [!WARNING]
> **Hatırlatma:** `ProductionKiosk=true` kurulumunda uygulanan tüm sistem değişiklikleri (kayıt defteri/politika değerleri, servis kaydı, ACL izinleri) kurulum anında yedeklenir ve yalnızca yönetici yetkisiyle geri alınabilir.

---

## 1. Normal Kaldırma (Önerilen Yöntem)

### Yöntem A — Kurulum Sihirbazı (GUI) Üzerinden
1. Bilgisayar erişimi açıkken veya Bakım Modunda **Sistem Tepsisi (Tray)** simgesine sağ tıklayıp **Yönetici Konsolu**na girin (ya da kilit ekranında Yönetici girişi ile Bakım Modunu açın).
2. Kurulum klasöründeki **`HomeworkGuard.Setup.exe`** uygulamasını yönetici olarak başlatın.
3. Sol alttaki **"Korumayı Kaldır"** butonuna tıklayın.
4. Bu işlem `HomeworkGuardService` servisini durdurup siler, ACL sıkılaştırmalarını kaldırır, kurulumda değiştirilen kayıt defteri/politika değerlerini ilk kurulum öncesi durumuna geri döndürür ve kurulum dosyalarını temizler.

### Yöntem B — PowerShell Betiği Üzerinden
Yönetici PowerShell oturumu açıp kurulum paketindeki kaldırma betiğini çalıştırabilirsiniz:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Uninstall-Hardening.ps1
```

---

## 2. Acil Kurtarma Senaryoları (Bilgisayar Kilitliyken)

### Senaryo 1 — Telegram Erişiminiz Varsa (En Hızlı Yol)
Telegram botunuza aşağıdaki komutlardan birini gönderin:
- **`/bakim_ac`**: Kiosk kilidini ve Watchdog'u anında durdurur, masaüstüne tam erişim açar.
- **`/ac`**: Bilgisayarı serbest kullanım moduna geçirir.
- **`/sifre_sifirla YeniSifre123`**: Yönetici şifrenizi unuttuysanız uzaktan sıfırlar.

### Senaryo 2 — Acil Kurtarma Anahtarı İle
Kurulum sırasında ekranda verilen **Acil Kurtarma Anahtarını** kilit ekranındaki yönetici giriş penceresine girerek Bakım Modunu açabilirsiniz.

### Senaryo 3 — Güvenli Mod (Safe Mode) ile Manuel Kurtarma
İnternet/Telegram erişiminiz yoksa ve yönetici şifresi ile kurtarma anahtarını da kaybettiyseniz:

1. Bilgisayarı yeniden başlatıp **Windows Güvenli Mod (Safe Mode with Command Prompt)** veya **Windows Kurtarma Ortamı (WinRE → Sorun Gider → Gelişmiş Seçenekler → Başlangıç Ayarları)** üzerinden açın.
2. `HomeworkGuardService` servisi Güvenli Modda devre dışıysa veya durdurulabiliyorsa Yönetici Komut İstemi'nde (CMD) çalıştırın:
   ```cmd
   sc stop HomeworkGuardService
   sc config HomeworkGuardService start= disabled
   taskkill /F /IM HomeworkGuard.UI.exe
   ```
3. Ardından kaldırma betiğini çalıştırın:
   ```cmd
   powershell -NoProfile -ExecutionPolicy Bypass -File "C:\Program Files\HomeworkGuard\Uninstall-Hardening.ps1"
   ```
4. Bilgisayarı normal kipte yeniden başlatın.

---

> [!NOTE]
> Kurulum öncesi kayıt defteri yedekleri standart kaldırma sırasında otomatik geri yüklenir. Manuel kurtarma sonrasında kayıt defteri politika anahtarlarınızı kontrol etmek isterseniz `Uninstall-Hardening.ps1` betiğini Güvenli Mod veya normal masaüstünde yönetici olarak bir kez çalıştırmanız yeterlidir.
