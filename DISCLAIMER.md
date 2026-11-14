# Sorumluluk Reddi Beyanı (Disclaimer of Liability)

> [!CAUTION]
> **ÖNEMLİ UYARI — LÜTFEN KURULUMDAN ÖNCE DİKKATLE OKUYUNUZ**  
> **HomeworkGuard**, Windows işletim sistemi üzerinde **Kiosk kilitleme**, **sistem politikası sıkılaştırma (hardening)**, **Görev Yöneticisi ve klavye kısayolu engelleme**, **Windows Servisi (Watchdog)** ve **dosya/dizin erişim kontrolü (ACL)** uygulayan güçlü bir ebeveyn kontrolü aracıdır.

---

## 1. Bilgisayarın Kilitlenmesi ve Sistem Değişiklikleri Hakkında

Bu yazılımı geliştirme ortamı dışında (**`ProductionKiosk=true`** ile) kendi fiziksel bilgisayarınıza veya bir sanal makineye kurduğunuzda aşağıdaki sistem düzeyinde değişiklikler yapılır:

- `HomeworkGuardService` adlı bir **Windows Servisi** (`LocalSystem`) kurulur ve otomatik başlatılır.
- Öğrenci oturumunda tam ekran, en üst katman (**Topmost**) kilit ekranı (`HomeworkGuard.UI.exe`) ve düşük seviyeli klavye kancası (`Alt+Tab`, `Win`, `Ctrl+Esc` vb. engellemesi) devreye alınır.
- Windows Kayıt Defteri (Registry) ve politika ayarları üzerinden **Görev Yöneticisi**, **Kayıt Defteri Düzenleyicisi**, **CMD** ve **Sistem Saati Değiştirme** gibi araçlar standart kullanıcılar için kısıtlanabilir.
- Kurulum dizini (`C:\Program Files\HomeworkGuard`) ve veri dizini (`C:\ProgramData\HomeworkGuard`) üzerindeki dosya izinleri (**ACL**) standart kullanıcıların silme veya değiştirme yapmasını önleyecek şekilde sıkılaştırılır.

## 2. Sorumluluk Reddi (No Liability)

1. **Kilitlenme Riski:** Yönetici şifresinin unutulması, kurtarma kodunun kaybedilmesi, yanlış saat/gün kuralı girilmesi, üçüncü parti güvenlik/antivirüs yazılımlarıyla çakışma veya hatalı kurulum/kaldırma işlemleri nedeniyle **bilgisayarın kilitlenmesi veya masaüstüne erişilememesi** durumunda **geliştiriciler ve katkıda bulunanlar hiçbir şekilde sorumluluk kabul etmez**.
2. **Veri Kaybı ve İş Kesintisi:** Yazılımın kullanımından, hatalı yapılandırılmasından veya sistem politikalarında yapılan değişikliklerden doğabilecek doğrudan ya da dolaylı hiçbir **veri kaybı, iş/zaman kaybı veya donanım/yazılım hasarından** yazarlar sorumlu tutulamaz.
3. **"Olduğu Gibi" Sunum:** Yazılım MIT Lisansı kapsamında **"OLDUĞU GİBİ" (AS IS)** sunulmaktadır. Hiçbir açık veya zımni garanti verilmez.
4. **Önerilen Kullanım:**
   - Üretim sürümünü (`ProductionKiosk=true`) ana çalışma bilgisayarınıza kurmadan önce **mutlaka bir Sanal Makinede (VirtualBox / VMware / Hyper-V)** ve **Snapshot (Anlık Görüntü)** alarak test ediniz.
   - Kurulum sırasında verilen **Acil Kurtarma Anahtarını** ve **Yönetici Şifresini** güvenli, bilgisayar dışındaki bir yerde saklayınız.
   - **Telegram uzaktan yönetim** entegrasyonunu mutlaka yapılandırıp `/durum` ve `/bakim_ac` komutlarının çalıştığını doğrulayınız.

---

## 3. Kurtarma ve Kaldırma

Kilitleme veya erişim sorunu yaşamanız durumunda izlemeniz gereken adımlar için [Kurtarma ve Kaldırma Kılavuzu](docs/RECOVERY_AND_UNINSTALL.md) belgesine bakınız.

---

## English Summary (Disclaimer)

**HomeworkGuard** applies OS-level kiosk lockdown, low-level keyboard hooks, a `LocalSystem` watchdog service, Windows Registry/policy restrictions, and hardened NTFS ACLs when built with `ProductionKiosk=true`.

By installing or running this software, you acknowledge that:
- Misconfiguration, forgotten admin credentials, or conflicts with system policies may **lock access to your desktop/PC**.
- The authors and contributors assume **NO LIABILITY** for any computer lockout, data loss, downtime, or system damage resulting from the use or misuse of this software.
- Always test `ProductionKiosk=true` builds inside an isolated **Virtual Machine with snapshots** first, and store your admin password and recovery key offline.
