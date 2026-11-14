# Telegram Bot Kurulumu ve Komutlar (`TELEGRAM_SETUP.md`)

**HomeworkGuard**, ebeveynlerin bilgisayarı uzaktan izleyip yönetebilmesi için Telegram Bot API entegrasyonu sunar.

---

## 1. Bot Token ve Admin Chat ID Alma

### Adım 1 — Bot Oluşturma (`@BotFather`)
1. Telegram'da **`@BotFather`** hesabını açın ve `/newbot` komutunu gönderin.
2. Botunuz için bir görünen ad (ör. `Ev Ödev Koruması`) ve `bot` ile biten bir kullanıcı adı (ör. `ev_odev_koruma_bot`) belirleyin.
3. `@BotFather` size `123456789:ABCdef...` biçiminde bir **Bot Token** verecektir. Bu değeri kurulum ekranındaki **Telegram Bot Token** alanına yapıştırın.
4. Oluşturduğunuz botun sohbetine girip mutlaka **`/start`** mesajı gönderin (botun size ilk mesajı atabilmesi için gereklidir).

### Adım 2 — Admin Chat ID Öğrenme
1. Telegram'da **`@userinfobot`** (veya `@raw_data_bot`) botunu açıp `/start` gönderin.
2. Dönen yanıttaki sayısal **`Id`** değerini (ör. `123456789`) kurulum ekranındaki **Admin Chat ID** alanına yazın.
3. **Güvenlik:** Bot yalnızca bu `Admin Chat ID`'den gelen komutları ve buton tıklamalarını kabul eder; diğer tüm sohbetlerden gelen istekler yok sayılır.

---

## 2. Desteklenen Telegram Komutları

Bot sohbetinde `/menu` veya `/start` yazdığınızda hızlı erişim için **Satır İçi Butonlar (Inline Keyboard)** da gönderilir.

| Komut | Açıklama |
| :--- | :--- |
| `/menu`, `/start`, `/yardim` | Etkileşimli kontrol menüsünü ve komut listesini açar. |
| `/durum` | Anlık kilit durumu, saat, kalan süre ve günün ödev özetini gösterir. |
| `/ac`, `/acil_ac` | Ödev beklenmeden bilgisayarı uzaktan serbest erişime açar. |
| `/kilitle`, `/kitle` | Bilgisayarı anında ödev kilit ekranına geri alır. |
| `/bakim_ac` | Yönetici Bakım Modunu etkinleştirir (kiosk ve kilidi geçici durdurur). |
| `/bakim_kapat` | Bakım Modunu kapatıp korumayı yeniden devreye alır. |
| `/gorev_ekle <metin>`, `/kriter <metin>` | Günün ödev kriterini uzaktan günceller. |
| `/ayar_saat <SS:dd> <SS:dd>` | Açılış ve Gece Kilidi saatlerini günceller (ör. `/ayar_saat 09:00 22:30`). |
| `/ayar_sure <dakika>` | Günlük kullanım limitini günceller (`0` = sınırsız). |
| `/gun_ayar <gun> ...` | Haftanın belirli bir günü için saat/süre/kapalı kuralı ayarlar. |
| `/program` | Pazartesi–Pazar haftalık çalışma programını listeler. |
| `/siteler` | Eğitim tarayıcısındaki izinli URL listesini gösterir. |
| `/site_ekle <url>` | Eğitim tarayıcısına yeni izinli site ekler. |
| `/site_sil <url>` | Eğitim tarayıcısından site kaldırır. |
| `/log` | Son sistem ve ödev denetim kayıtlarını gönderir. |
| `/acil_kod` | Tek kullanımlık / güncel acil kurtarma kodunu üretir veya gösterir. |
| `/sifre_sifirla <yeni_sifre>` | Yönetici şifresini uzaktan sıfırlar (en az 6 karakter). |

---

## 3. Otomatik Bildirimler (Canlı Loglama)

- Öğrenci Aşama 1 için dosya/fotoğraf yüklediğinde dosya önizlemesi/bilgisi ve Gemini'nin ilk değerlendirmesi Telegram'a gönderilir.
- Öğrenci Aşama 2 Sokratik sorularını yanıtladığında her iki soru, öğrencinin yazdığı cevaplar, aldığı puan (`0-100`) ve onay/red sonucu anında yöneticiye iletilir.
- Yetkisiz kapatma/müdahale girişimleri ve kilit durumu değişiklikleri anlık bildirim olarak düşer.
