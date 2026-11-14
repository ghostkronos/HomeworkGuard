using HomeworkGuard.Core.Models;
using HomeworkGuard.Service.Engine;
using HomeworkGuard.Service.Security;
using HomeworkGuard.Service.Storage;
using Telegram.Bot;
using Telegram.Bot.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Types.ReplyMarkups;

namespace HomeworkGuard.Service.Telegram;

/// <summary>
/// Full remote management Telegram Bot & live student activity notification service.
/// Strictly restricts all commands and inline callbacks to the configured authorized Admin ChatID.
/// </summary>
public sealed class TelegramAdminBotService : ITelegramNotifier, IDisposable
{
    private readonly DatabaseService _db;
    private readonly HomeworkGuardCoordinator _coordinator;
    private readonly AdminSecurityService _security;
    private readonly ILogger<TelegramAdminBotService> _logger;

    private readonly SemaphoreSlim _clientLock = new(1, 1);
    private TelegramBotClient? _client;
    private string _activeToken = string.Empty;
    private CancellationTokenSource? _pollingCts;

    public TelegramAdminBotService(
        DatabaseService db,
        HomeworkGuardCoordinator coordinator,
        AdminSecurityService security,
        ILogger<TelegramAdminBotService> logger)
    {
        _db = db;
        _coordinator = coordinator;
        _security = security;
        _logger = logger;
        _coordinator.AttachTelegramNotifier(this);
    }

    public bool IsReady => _client is not null && _db.GetAdminChatId().HasValue;

    public async Task EnsureRunningAsync(CancellationToken hostCt)
    {
        string configuredToken = _db.GetTelegramBotToken();
        if (string.IsNullOrWhiteSpace(configuredToken))
        {
            return;
        }

        await _clientLock.WaitAsync(hostCt);
        try
        {
            if (_client is not null && string.Equals(_activeToken, configuredToken, StringComparison.Ordinal))
            {
                return;
            }

            _pollingCts?.Cancel();
            _pollingCts?.Dispose();
            _pollingCts = CancellationTokenSource.CreateLinkedTokenSource(hostCt);

            _client = new TelegramBotClient(configuredToken);
            _activeToken = configuredToken;

            var receiverOptions = new ReceiverOptions
            {
                AllowedUpdates = [UpdateType.Message, UpdateType.CallbackQuery]
            };

            _client.StartReceiving(
                updateHandler: HandleUpdateAsync,
                errorHandler: HandlePollingErrorAsync,
                receiverOptions: receiverOptions,
                cancellationToken: _pollingCts.Token);

            _logger.LogInformation("Telegram Admin Bot dinlemeye başladı (Komut + Inline Kontrol + Canlı Log).");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram Bot başlatılamadı.");
        }
        finally
        {
            _clientLock.Release();
        }
    }

    private async Task HandleUpdateAsync(ITelegramBotClient bot, Update update, CancellationToken ct)
    {
        long? authorizedChatId = _db.GetAdminChatId();

        if (update.CallbackQuery is { } cb)
        {
            long chatId = cb.Message?.Chat.Id ?? cb.From.Id;
            if (!authorizedChatId.HasValue || chatId != authorizedChatId.Value)
            {
                await bot.AnswerCallbackQuery(cb.Id, "Yetkisiz erişim.", showAlert: true, cancellationToken: ct);
                _db.AddAuditLog("TELEGRAM_UNAUTHORIZED", chatId.ToString(), $"Yetkisiz buton tıklaması: {cb.Data}");
                return;
            }

            await bot.AnswerCallbackQuery(cb.Id, cancellationToken: ct);
            string action = cb.Data ?? "/durum";
            var (reply, includeMenu) = ExecuteCommand(action, string.Empty);
            await SendReplySafeAsync(bot, chatId, reply, includeMenu ? BuildControlKeyboard() : null, ct);
            return;
        }

        if (update.Message is not { } msg) return;
        if (string.IsNullOrWhiteSpace(msg.Text)) return;

        if (!authorizedChatId.HasValue || msg.Chat.Id != authorizedChatId.Value)
        {
            _db.AddAuditLog("TELEGRAM_UNAUTHORIZED", msg.Chat.Id.ToString(), $"Yetkisiz Telegram mesajı yoksayıldı: {msg.Text}");
            return;
        }

        string text = msg.Text.Trim();
        string[] parts = text.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        string command = parts[0].ToLowerInvariant();
        string arg = parts.Length > 1 ? parts[1].Trim() : string.Empty;

        int atIdx = command.IndexOf('@');
        if (atIdx > 0) command = command[..atIdx];

        var (replyText, withKeyboard) = ExecuteCommand(command, arg);
        await SendReplySafeAsync(bot, msg.Chat.Id, replyText, withKeyboard ? BuildControlKeyboard() : BuildQuickReturnKeyboard(), ct);
    }

    private static async Task SendReplySafeAsync(
        ITelegramBotClient bot,
        long chatId,
        string text,
        InlineKeyboardMarkup? markup,
        CancellationToken ct)
    {
        try
        {
            await bot.SendMessage(chatId, text, parseMode: ParseMode.Markdown, replyMarkup: markup, cancellationToken: ct);
        }
        catch
        {
            // Fallback without Markdown if dynamic user/log text contains unescaped characters
            await bot.SendMessage(chatId, text, replyMarkup: markup, cancellationToken: ct);
        }
    }

    private (string Reply, bool ShowFullMenu) ExecuteCommand(string command, string arg)
    {
        switch (command)
        {
            case "/start":
            case "/yardim":
            case "/menu":
                return ("""
                    🛡️ *HomeworkGuard Tam Uzaktan Kontrol Paneli*

                    Aşağıdaki hızlı kontrol butonlarını kullanabilir veya komut yazabilirsiniz:
                    • `/durum` — Anlık durum, kalan süre, bakım/kilit bilgisi
                    • `/ac` veya `/acil_ac` — Bugünlük bilgisayarı aç
                    • `/kilitle` — Bilgisayarı anında kilitle
                    • `/bakim_ac` — Tam Yönetici (Bakım) Modu aç
                    • `/bakim_kapat` — Bakımdan çık ve korumayı hemen başlat
                    • `/acil_kod` — 5 dk geçerli 6 haneli Admin OTP üret
                    • `/gorev_ekle [metin]` — Günlük ödev kriterini güncelle
                    • `/ayar_saat HH:mm HH:mm` — Tüm günlerin saatini değiştir
                    • `/ayar_sure [dakika]` — Tüm günlerin dakika limitini değiştir (`0` = sınırsız)
                    • `/gun_ayar [gün] [dk|sinirsiz|kapali] [SS:dd-SS:dd]` — Gün bazlı kural (örn: `/gun_ayar cumartesi 180 10:00-23:00`)
                    • `/program` — 7 günlük haftalık planı göster
                    • `/siteler`, `/site_ekle [url]`, `/site_sil [url]` — Ders tarayıcısı yönetimi
                    • `/log [adet]` — Son sistem & öğrenci hareketlerini gör
                    • `/sifre_sifirla [yeni_sifre]` — Admin şifresini sıfırla
                    """, true);

            case "/gorev_ekle":
            case "/kriter":
                if (string.IsNullOrWhiteSpace(arg))
                    return ("⚠️ Kullanım: `/gorev_ekle Matematik kesirler 15 soru çözülmeli`", false);
                _db.UpdateDailyCriteria(arg);
                _db.AddAuditLog("CRITERIA_UPDATED", "AdminTelegram", $"Yeni kriter: {arg}");
                return ($"✅ Günlük ödev kriteri güncellendi:\n_{arg}_", false);

            case "/ayar_saat":
            {
                var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length == 2 &&
                    TimeSpan.TryParseExact(tokens[0], @"hh\:mm", null, out var startTs) &&
                    TimeSpan.TryParseExact(tokens[1], @"hh\:mm", null, out var endTs))
                {
                    _coordinator.UpdateSchedule(startTs.Hours, startTs.Minutes, endTs.Hours, endTs.Minutes, "AdminTelegram");
                    return ($"🕒 Tüm günler için kullanım saatleri güncellendi: `{startTs:hh\\:mm} - {endTs:hh\\:mm}`", false);
                }
                return ("⚠️ Kullanım: `/ayar_saat 10:00 22:00`", false);
            }

            case "/ayar_sure":
            {
                if (int.TryParse(arg, out int mins) && mins >= 0)
                {
                    _coordinator.UpdateMaxUsageMinutes(mins, "AdminTelegram");
                    return (mins == 0
                        ? "⏱️ Tüm günler için günlük süre limiti kaldırıldı (Sınırsız)."
                        : $"⏱️ Tüm günler için günlük süre limiti `{mins} dakika` olarak ayarlandı.", false);
                }
                return ("⚠️ Kullanım: `/ayar_sure 120` (dakika; sınırsız için `0`)", false);
            }

            case "/gun_ayar":
            {
                var tokens = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (tokens.Length < 2 || !TryParseTurkishDay(tokens[0], out DayOfWeek day))
                {
                    return ("⚠️ Kullanım: `/gun_ayar pazartesi 90 10:00-21:30` veya `/gun_ayar cumartesi sinirsiz` veya `/gun_ayar sali kapali`", false);
                }

                string quotaToken = tokens[1].ToLowerInvariant();
                bool isBlocked = quotaToken is "kapali" or "kapalı" or "bloke";
                int minutes = 0;
                if (!isBlocked && quotaToken is not ("sinirsiz" or "sınırsız" or "0"))
                {
                    if (!int.TryParse(quotaToken, out minutes) || minutes < 0)
                    {
                        return ("⚠️ Süre dakika cinsinden pozitif tam sayı, `sinirsiz` veya `kapali` olmalıdır.", false);
                    }
                }

                int? sh = null, sm = null, eh = null, em = null;
                if (tokens.Length >= 3)
                {
                    var range = tokens[2].Split('-', StringSplitOptions.RemoveEmptyEntries);
                    if (range.Length == 2 &&
                        TimeSpan.TryParseExact(range[0], @"hh\:mm", null, out var sTs) &&
                        TimeSpan.TryParseExact(range[1], @"hh\:mm", null, out var eTs))
                    {
                        sh = sTs.Hours; sm = sTs.Minutes;
                        eh = eTs.Hours; em = eTs.Minutes;
                    }
                    else
                    {
                        return ("⚠️ Saat aralığı biçimi `HH:mm-HH:mm` olmalıdır (örn: `10:00-22:30`).", false);
                    }
                }

                _coordinator.UpdateDaySchedule(day, sh, sm, eh, em, minutes, isBlocked, "AdminTelegram");
                return ($"✅ *{HomeworkRuleConfig.GetTurkishDayName(day)}* güncellendi.\n\n{_coordinator.BuildWeeklyScheduleReport()}", false);
            }

            case "/program":
                return (_coordinator.BuildWeeklyScheduleReport(), false);

            case "/siteler":
                return (_coordinator.BuildWhitelistReport(), false);

            case "/site_ekle":
                if (string.IsNullOrWhiteSpace(arg))
                    return ("⚠️ Kullanım: `/site_ekle https://ornek-egitim.gov.tr`", false);
                return ($"🌐 {_coordinator.ModifyWhitelistSite(arg, add: true, "AdminTelegram").Message}", false);

            case "/site_sil":
                if (string.IsNullOrWhiteSpace(arg))
                    return ("⚠️ Kullanım: `/site_sil ornek-egitim.gov.tr`", false);
                return ($"🌐 {_coordinator.ModifyWhitelistSite(arg, add: false, "AdminTelegram").Message}", false);

            case "/log":
            {
                int count = int.TryParse(arg, out int n) ? n : 12;
                return (_coordinator.BuildRecentLogsReport(count), false);
            }

            case "/durum":
                return (_coordinator.BuildTelegramStatusReport(), true);

            case "/ac":
            case "/acil_ac":
                _coordinator.ForceUnlockToday("AdminTelegram");
                return ("🔓 Bilgisayar kilidi bugün için uzaktan açıldı.", true);

            case "/kilitle":
            case "/kitle":
                _coordinator.ForceLockNow("AdminTelegram");
                return ("🔒 Bilgisayar uzaktan anında kilitlendi.", true);

            case "/bakim_ac":
                _coordinator.EnterMaintenanceMode("AdminTelegram");
                return ("🛠️ Tam Yönetici (Bakım) Modu uzaktan AÇILDI. Kiosk ekranı gizlendi (tepsiye küçültüldü) ve dosya/OS kısıtlamaları geçici olarak durduruldu.", true);

            case "/bakim_kapat":
                _coordinator.ExitMaintenanceMode("AdminTelegram");
                return ("🛡️ Bakım Modu kapatıldı. Watchdog, Kiosk kilidi ve dosya koruma kilitleri hemen tekrar devrede.", true);

            case "/acil_kod":
            {
                string otp = _security.IssueEmergencyOtp();
                return ($"🔑 Acil Durum OTP Kodunuz: `{otp}`\nBu kod 5 dakika geçerlidir. Admin giriş penceresine yazabilirsiniz.", false);
            }

            case "/sifre_sifirla":
                if (string.IsNullOrWhiteSpace(arg) || arg.Length < 6)
                    return ("⚠️ Yeni şifre en az 6 karakter olmalıdır. Kullanım: `/sifre_sifirla YeniGucluSifre123`", false);
                _security.ResetPasswordFromTelegram(arg);
                return ("✅ Admin paneli şifresi başarıyla güncellendi ve olası giriş kilitleri kaldırıldı.", false);

            default:
                return ("❓ Bilinmeyen komut. Kontrol menüsü için `/menu` veya `/yardim` yazın.", true);
        }
    }

    private static InlineKeyboardMarkup BuildControlKeyboard() => new(
    [
        [
            InlineKeyboardButton.WithCallbackData("📊 Durum", "/durum"),
            InlineKeyboardButton.WithCallbackData("🔓 Bugünlük Aç", "/ac")
        ],
        [
            InlineKeyboardButton.WithCallbackData("🔒 Hemen Kilitle", "/kilitle"),
            InlineKeyboardButton.WithCallbackData("🛠️ Bakım Aç", "/bakim_ac")
        ],
        [
            InlineKeyboardButton.WithCallbackData("🛡️ Bakım Kapat", "/bakim_kapat"),
            InlineKeyboardButton.WithCallbackData("🔑 Acil OTP", "/acil_kod")
        ],
        [
            InlineKeyboardButton.WithCallbackData("📅 Haftalık Program", "/program"),
            InlineKeyboardButton.WithCallbackData("📜 Son Loglar", "/log")
        ],
        [
            InlineKeyboardButton.WithCallbackData("🌐 İzinli Siteler", "/siteler")
        ]
    ]);

    private static InlineKeyboardMarkup BuildQuickReturnKeyboard() => new(
    [
        [
            InlineKeyboardButton.WithCallbackData("📋 Kontrol Menüsü", "/menu"),
            InlineKeyboardButton.WithCallbackData("📊 Durum", "/durum")
        ]
    ]);

    private static bool TryParseTurkishDay(string input, out DayOfWeek day)
    {
        string normalized = input.Trim().ToLowerInvariant()
            .Replace('ı', 'i').Replace('ş', 's').Replace('ğ', 'g')
            .Replace('ü', 'u').Replace('ö', 'o').Replace('ç', 'c');

        switch (normalized)
        {
            case "pzt": case "pazartesi": case "monday": case "mon":
                day = DayOfWeek.Monday; return true;
            case "sal": case "sali": case "tuesday": case "tue":
                day = DayOfWeek.Tuesday; return true;
            case "car": case "carsamba": case "wednesday": case "wed":
                day = DayOfWeek.Wednesday; return true;
            case "per": case "persembe": case "thursday": case "thu":
                day = DayOfWeek.Thursday; return true;
            case "cum": case "cuma": case "friday": case "fri":
                day = DayOfWeek.Friday; return true;
            case "cmt": case "ctesi": case "cumartesi": case "saturday": case "sat":
                day = DayOfWeek.Saturday; return true;
            case "paz": case "pazar": case "sunday": case "sun":
                day = DayOfWeek.Sunday; return true;
            default:
                day = default; return false;
        }
    }

    private Task HandlePollingErrorAsync(ITelegramBotClient bot, Exception ex, HandleErrorSource source, CancellationToken ct)
    {
        _logger.LogWarning(ex, "Telegram polling hatası ({Source})", source);
        return Task.CompletedTask;
    }

    public async Task SendAdminTextAsync(string message, CancellationToken ct = default)
    {
        long? chatId = _db.GetAdminChatId();
        if (!chatId.HasValue || _client is null) return;

        try
        {
            string trimmed = message.Length > 3900 ? message[..3900] + "..." : message;
            await _client.SendMessage(chatId.Value, trimmed, cancellationToken: ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram admin mesajı gönderilemedi.");
        }
    }

    public async Task SendSubmissionMediaLogAsync(
        string filePath,
        string fileName,
        string caption,
        CancellationToken ct = default)
    {
        long? chatId = _db.GetAdminChatId();
        if (!chatId.HasValue || _client is null) return;

        if (caption.Length > 1000) caption = caption[..1000] + "...";

        try
        {
            if (File.Exists(filePath))
            {
                string ext = Path.GetExtension(fileName).ToLowerInvariant();
                await using var stream = File.OpenRead(filePath);
                var inputFile = InputFile.FromStream(stream, fileName);

                if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")
                {
                    await _client.SendPhoto(chatId.Value, inputFile, caption: caption, cancellationToken: ct);
                }
                else
                {
                    await _client.SendDocument(chatId.Value, inputFile, caption: caption, cancellationToken: ct);
                }
            }
            else
            {
                await _client.SendMessage(chatId.Value, caption, cancellationToken: ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Telegram ödev medya logu gönderilemedi.");
        }
    }

    public Task SendHomeworkApprovedReportAsync(
        string filePath,
        string fileName,
        int score,
        string summary,
        string feedback,
        CancellationToken ct = default)
    {
        string caption = $"""
            ✅ Ödev AI Tarafından Onaylandı!
            📊 Puan: {score}/100
            📝 Özet: {summary}
            💬 AI Notu: {feedback}
            """;
        return SendSubmissionMediaLogAsync(filePath, fileName, caption, ct);
    }

    public void Dispose()
    {
        _pollingCts?.Cancel();
        _pollingCts?.Dispose();
        _clientLock.Dispose();
    }
}
