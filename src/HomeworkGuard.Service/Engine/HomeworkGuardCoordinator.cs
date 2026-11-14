using System.Text;
using System.Text.Json;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.Core.Models;
using HomeworkGuard.Core.Security;
using HomeworkGuard.Service.AI;
using HomeworkGuard.Service.Security;
using HomeworkGuard.Service.Storage;
using HomeworkGuard.Service.Time;

namespace HomeworkGuard.Service.Engine;

public interface ITelegramNotifier
{
    Task SendAdminTextAsync(string message, CancellationToken ct = default);
    Task SendSubmissionMediaLogAsync(string filePath, string fileName, string caption, CancellationToken ct = default);
    Task SendHomeworkApprovedReportAsync(string filePath, string fileName, int score, string summary, string feedback, CancellationToken ct = default);
}

/// <summary>
/// Central state and workflow coordinator for HomeworkGuard.Service.
/// Handles IPC requests, two-stage AI verification state, admin overrides, and Telegram commands.
/// </summary>
public sealed class HomeworkGuardCoordinator
{
    private readonly DatabaseService _db;
    private readonly TrustedTimeService _timeService;
    private readonly AdminSecurityService _securityService;
    private readonly GeminiHomeworkEvaluator _gemini;
    private readonly TamperProtectionService? _tamperProtection;
    private readonly ILogger<HomeworkGuardCoordinator> _logger;
    private ITelegramNotifier? _telegramNotifier;

    private readonly object _sessionLock = new();
    private bool _isMaintenanceMode;
    private HomeworkEvaluationStage _evalStage = HomeworkEvaluationStage.Idle;
    private List<string> _socraticQuestions = [];
    private string _lastAiFeedback = string.Empty;
    private int? _lastScore;
    private PendingSubmission? _pendingSubmission;
    private string _lastBrowserEventSignature = string.Empty;
    private DateTime _lastBrowserEventUtc = DateTime.MinValue;

    private sealed record PendingSubmission(
        string FileName,
        string MimeType,
        byte[] FileBytes,
        string SavedFilePath,
        string Stage1Summary,
        List<string> Questions);

    public HomeworkGuardCoordinator(
        DatabaseService db,
        TrustedTimeService timeService,
        AdminSecurityService securityService,
        GeminiHomeworkEvaluator gemini,
        ILogger<HomeworkGuardCoordinator> logger,
        TamperProtectionService? tamperProtection = null)
    {
        _db = db;
        _timeService = timeService;
        _securityService = securityService;
        _gemini = gemini;
        _logger = logger;
        _tamperProtection = tamperProtection;

        _securityService.OnSecurityBreachDetected += async msg =>
        {
            if (_telegramNotifier is not null)
            {
                await _telegramNotifier.SendAdminTextAsync(msg);
            }
        };
    }

    public void AttachTelegramNotifier(ITelegramNotifier notifier)
    {
        _telegramNotifier = notifier;
    }

    public bool IsMaintenanceMode
    {
        get { lock (_sessionLock) return _isMaintenanceMode; }
    }

    public ServiceRuntimeStatus GetCurrentStatus()
    {
        DateTime utcNow = _timeService.GetTrustedUtcNow();
        DateTime localNow = TimeZoneInfo.ConvertTimeFromUtc(utcNow, TimeZoneInfo.Local);
        string todayKey = localNow.ToString("yyyy-MM-dd");

        var rules = _db.GetRuleConfig();
        bool isTodayApproved = _db.IsDateApproved(todayKey);
        int usedSecondsToday = isTodayApproved ? _db.GetUsedSecondsForDate(todayKey) : 0;
        var (failedAttempts, lockoutUntilUtc, _, _) = _db.GetSecurityState();

        bool maintenance;
        HomeworkEvaluationStage stage;
        List<string> questions;
        string feedback;
        int? score;

        lock (_sessionLock)
        {
            maintenance = _isMaintenanceMode;
            stage = _evalStage;
            questions = [.. _socraticQuestions];
            feedback = _lastAiFeedback;
            score = _lastScore;
        }

        bool internetOk = _timeService.IsInternetAvailable;
        var todayRule = rules.GetEffectiveRuleForDay(localNow.DayOfWeek);
        var (state, showWarning, remainingText, statusMsg) = TrustedTimeService.EvaluateState(
            localNow, rules, isTodayApproved, maintenance, internetOk, usedSecondsToday);

        return new ServiceRuntimeStatus
        {
            CurrentState = state,
            IsDevSandboxMode = AppBuildConfig.IS_DEV_SANDBOX_MODE,
            EffectiveUtcNow = utcNow,
            EffectiveLocalNow = localNow,
            IsTimeSyncedWithNtp = _timeService.IsTimeSyncedWithNtp,
            IsInternetAvailable = internetOk,
            IsMaintenanceMode = maintenance,
            IsTodayApproved = isTodayApproved,
            ApprovedDate = isTodayApproved ? todayKey : string.Empty,
            DailyCriteria = rules.DailyHomeworkCriteria,
            WhitelistUrls = rules.WhitelistUrls,
            StartHour = todayRule.StartHour,
            StartMinute = todayRule.StartMinute,
            EndHour = todayRule.EndHour,
            EndMinute = todayRule.EndMinute,
            WarningMinutesBeforeLock = rules.WarningMinutesBeforeLock,
            DailyMaxUsageMinutes = todayRule.MaxUsageMinutes,
            UsedMinutesToday = usedSecondsToday / 60,
            IsTodayBlocked = todayRule.IsDayBlocked,
            EffectiveDayName = HomeworkRuleConfig.GetTurkishDayName(localNow.DayOfWeek),
            WeeklySchedule = rules.WeeklySchedule,
            GeminiModel = rules.GeminiModel,
            FailedAdminAttempts = failedAttempts,
            AdminLockoutUntilUtc = lockoutUntilUtc,
            StatusMessage = statusMsg,
            ShowPreLockWarning = showWarning,
            EvaluationStage = stage,
            SocraticQuestions = questions,
            LastAiFeedback = feedback,
            LastScore = score,
            RemainingFreeTimeText = remainingText
        };
    }

    public async Task<IpcResponse> HandleIpcRequestAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            switch (request.Command)
            {
                case IpcCommands.GetStatus:
                    return IpcResponse.Ok(request.CorrelationId, "OK", GetCurrentStatus());

                case IpcCommands.ResetHomeworkSession:
                    ResetEvaluationSession();
                    return IpcResponse.Ok(request.CorrelationId, "Oturum sıfırlandı.", GetCurrentStatus());

                case IpcCommands.SubmitHomeworkMedia:
                {
                    var payload = DeserializePayload<SubmitHomeworkMediaRequest>(request.PayloadJson);
                    if (payload is null) return IpcResponse.Fail(request.CorrelationId, "Geçersiz ödev verisi.");
                    return await HandleStage1SubmissionAsync(request.CorrelationId, payload, ct);
                }

                case IpcCommands.SubmitSocraticAnswers:
                {
                    var payload = DeserializePayload<SubmitSocraticAnswersRequest>(request.PayloadJson);
                    if (payload is null) return IpcResponse.Fail(request.CorrelationId, "Geçersiz cevap verisi.");
                    return await HandleStage2AnswersAsync(request.CorrelationId, payload, ct);
                }

                case IpcCommands.AuthenticateAdmin:
                {
                    var payload = DeserializePayload<AdminAuthRequest>(request.PayloadJson);
                    if (payload is null) return IpcResponse.Fail(request.CorrelationId, "Geçersiz kimlik bilgisi.");
                    var result = _securityService.Authenticate(payload.Credential);
                    return IpcResponse.Ok(request.CorrelationId,
                        result.Authenticated ? "Giriş başarılı." : "Giriş reddedildi.",
                        result);
                }

                case IpcCommands.AdminForceUnlock:
                {
                    var payload = DeserializePayload<AdminSessionRequest>(request.PayloadJson);
                    if (!_securityService.ValidateSessionToken(payload?.SessionToken))
                        return IpcResponse.Fail(request.CorrelationId, "Yetkisiz veya süresi dolmuş yönetici oturumu.", "UNAUTHORIZED");

                    ForceUnlockToday("AdminConsole");
                    return IpcResponse.Ok(request.CorrelationId, "Kilit bugün için açıldı.", GetCurrentStatus());
                }

                case IpcCommands.AdminForceLock:
                {
                    var payload = DeserializePayload<AdminSessionRequest>(request.PayloadJson);
                    if (!_securityService.ValidateSessionToken(payload?.SessionToken))
                        return IpcResponse.Fail(request.CorrelationId, "Yetkisiz veya süresi dolmuş yönetici oturumu.", "UNAUTHORIZED");

                    ForceLockNow("AdminConsole");
                    return IpcResponse.Ok(request.CorrelationId, "Bilgisayar anında kilitlendi.", GetCurrentStatus());
                }

                case IpcCommands.AdminEnterMaintenanceMode:
                {
                    var payload = DeserializePayload<AdminSessionRequest>(request.PayloadJson);
                    if (!_securityService.ValidateSessionToken(payload?.SessionToken))
                        return IpcResponse.Fail(request.CorrelationId, "Yetkisiz veya süresi dolmuş yönetici oturumu.", "UNAUTHORIZED");

                    EnterMaintenanceMode("AdminConsole");
                    return IpcResponse.Ok(request.CorrelationId, "Bakım modu etkinleştirildi. Watchdog durduruldu.", GetCurrentStatus());
                }

                case IpcCommands.AdminExitMaintenanceMode:
                {
                    var payload = DeserializePayload<AdminSessionRequest>(request.PayloadJson);
                    if (!_securityService.ValidateSessionToken(payload?.SessionToken))
                        return IpcResponse.Fail(request.CorrelationId, "Yetkisiz veya süresi dolmuş yönetici oturumu.", "UNAUTHORIZED");

                    ExitMaintenanceMode("AdminConsole");
                    return IpcResponse.Ok(request.CorrelationId, "Bakım modu kapatıldı.", GetCurrentStatus());
                }

                case IpcCommands.AdminGetSettings:
                {
                    var payload = DeserializePayload<AdminSessionRequest>(request.PayloadJson);
                    if (!_securityService.ValidateSessionToken(payload?.SessionToken))
                        return IpcResponse.Fail(request.CorrelationId, "Yetkisiz veya süresi dolmuş yönetici oturumu.", "UNAUTHORIZED");

                    return IpcResponse.Ok(request.CorrelationId, "OK", BuildAdminSettingsDto());
                }

                case IpcCommands.AdminUpdateSettings:
                {
                    var payload = DeserializePayload<AdminUpdateSettingsRequest>(request.PayloadJson);
                    if (payload is null || !_securityService.ValidateSessionToken(payload.SessionToken))
                        return IpcResponse.Fail(request.CorrelationId, "Yetkisiz veya süresi dolmuş yönetici oturumu.", "UNAUTHORIZED");

                    ApplyAdminSettingsUpdate(payload);
                    return IpcResponse.Ok(request.CorrelationId, "Ayarlar kaydedildi.", BuildAdminSettingsDto());
                }

                case IpcCommands.AdminRotateRecoveryKey:
                {
                    var payload = DeserializePayload<AdminSessionRequest>(request.PayloadJson);
                    if (!_securityService.ValidateSessionToken(payload?.SessionToken))
                        return IpcResponse.Fail(request.CorrelationId, "Yetkisiz veya süresi dolmuş yönetici oturumu.", "UNAUTHORIZED");

                    string newKey = _db.GenerateAndStoreNewMasterRecoveryKey();
                    _db.AddAuditLog("RECOVERY_KEY_ROTATED", "AdminConsole", "Yeni 24 haneli Master Recovery Key oluşturuldu.");
                    NotifyTelegram($"🔑 Master Recovery Key Yönetici Konsolu üzerinden yenilendi.");
                    return IpcResponse.Ok(request.CorrelationId, "Yeni kurtarma anahtarı oluşturuldu.", new AdminRotateRecoveryKeyResponse
                    {
                        NewRecoveryKey = newKey
                    });
                }

                case IpcCommands.ReportClientEvent:
                {
                    var payload = DeserializePayload<ClientActivityEventRequest>(request.PayloadJson);
                    if (payload is not null && !string.IsNullOrWhiteSpace(payload.EventType))
                    {
                        RecordStudentActivityEvent(payload.EventType, payload.Details);
                    }
                    return IpcResponse.Ok(request.CorrelationId, "OK");
                }

                default:
                    return IpcResponse.Fail(request.CorrelationId, $"Bilinmeyen komut: {request.Command}");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "IPC isteği işlenirken hata: {Command}", request.Command);
            return IpcResponse.Fail(request.CorrelationId, $"Sunucu hatası: {ex.Message}");
        }
    }

    private async Task<IpcResponse> HandleStage1SubmissionAsync(
        string correlationId,
        SubmitHomeworkMediaRequest req,
        CancellationToken ct)
    {
        var currentStatus = GetCurrentStatus();
        if (currentStatus.CurrentState == LockState.NightLock)
        {
            return IpcResponse.Fail(correlationId, currentStatus.StatusMessage, "NIGHT_LOCK");
        }

        if (!_timeService.IsInternetAvailable)
        {
            return IpcResponse.Fail(correlationId, "Ödev kontrolü için internet bağlantısı bekleniyor.", "NO_INTERNET");
        }

        byte[] fileBytes;
        try
        {
            fileBytes = Convert.FromBase64String(req.Base64Data);
        }
        catch (FormatException)
        {
            return IpcResponse.Fail(correlationId, "Gönderilen dosya formatı geçersiz.");
        }

        if (fileBytes.Length == 0 || fileBytes.Length > 15 * 1024 * 1024)
        {
            return IpcResponse.Fail(correlationId, "Dosya boş veya 15 MB sınırını aşıyor.");
        }

        string safeFileName = Path.GetFileName(string.IsNullOrWhiteSpace(req.FileName) ? "odev.jpg" : req.FileName);
        string savedPath = Path.Combine(
            _db.SubmissionsDirectory,
            $"{_timeService.GetTrustedLocalNow():yyyyMMdd_HHmmss}_{Guid.NewGuid():N}_{safeFileName}");
        await File.WriteAllBytesAsync(savedPath, fileBytes, ct);

        lock (_sessionLock)
        {
            _evalStage = HomeworkEvaluationStage.Stage1AnalyzingMedia;
            _lastAiFeedback = "Ödeviniz Gemini AI tarafından analiz ediliyor...";
            _socraticQuestions = [];
            _lastScore = null;
        }

        string apiKey = _db.GetGeminiApiKey();
        var rules = _db.GetRuleConfig();
        string criteria = rules.DailyHomeworkCriteria;
        string dateLocal = _timeService.GetTrustedLocalNow().ToString("yyyy-MM-dd");

        try
        {
            var stage1 = await _gemini.EvaluateStage1Async(
                apiKey, criteria, fileBytes, req.MimeType, safeFileName, req.StudentNote, ct, rules.GeminiModel);

            if (!stage1.PassedStage1)
            {
                lock (_sessionLock)
                {
                    _evalStage = HomeworkEvaluationStage.Rejected;
                    _lastAiFeedback = stage1.Feedback;
                    _socraticQuestions = [];
                    _pendingSubmission = null;
                }

                _db.AddHomeworkAttempt(dateLocal, "Stage1_Rejected", false, null, stage1.Feedback);
                _db.AddAuditLog("HOMEWORK_SUBMITTED_REJECTED", "Student", $"Dosya: {safeFileName} | Not: {req.StudentNote}");

                string rejectCaption = $"❌ Öğrenci Ödev Gönderdi (Aşama 1 Reddedildi)\n📎 Dosya: {safeFileName}" +
                    (string.IsNullOrWhiteSpace(req.StudentNote) ? "" : $"\n🗒️ Öğrenci Notu: {req.StudentNote}") +
                    $"\n💬 AI Geri Bildirimi: {stage1.Feedback}";
                NotifyTelegramMedia(savedPath, safeFileName, rejectCaption);

                return IpcResponse.Ok(correlationId, stage1.Feedback, GetCurrentStatus());
            }

            lock (_sessionLock)
            {
                _evalStage = HomeworkEvaluationStage.Stage2SocraticQuestions;
                _lastAiFeedback = stage1.Feedback;
                _socraticQuestions = stage1.SocraticQuestions;
                _pendingSubmission = new PendingSubmission(
                    safeFileName, req.MimeType, fileBytes, savedPath, stage1.DetectedSummary, stage1.SocraticQuestions);
            }

            _db.AddHomeworkAttempt(dateLocal, "Stage1_Passed", false, null, stage1.DetectedSummary);
            _db.AddAuditLog("HOMEWORK_SUBMITTED_STAGE1", "Student", $"Dosya: {safeFileName} | Özet: {Truncate(stage1.DetectedSummary, 140)}");

            string qList = string.Join("\n", stage1.SocraticQuestions.Select((q, i) => $"{i + 1}) {q}"));
            string passCaption = $"📥 Öğrenci Yeni Ödev Gönderdi (Aşama 1 Geçildi)\n📎 Dosya: {safeFileName}" +
                (string.IsNullOrWhiteSpace(req.StudentNote) ? "" : $"\n🗒️ Öğrenci Notu: {req.StudentNote}") +
                $"\n📝 AI Özeti: {stage1.DetectedSummary}\n❓ Sorulan 2 Soru:\n{qList}";
            NotifyTelegramMedia(savedPath, safeFileName, passCaption);

            return IpcResponse.Ok(correlationId, "İlk aşama geçildi. Sokratik soruları yanıtlayın.", GetCurrentStatus());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini Stage 1 hatası");
            lock (_sessionLock)
            {
                _evalStage = HomeworkEvaluationStage.Rejected;
                _lastAiFeedback = $"AI analiz hatası: {ex.Message}";
            }
            return IpcResponse.Fail(correlationId, $"AI değerlendirme hatası: {ex.Message}", null, GetCurrentStatus());
        }
    }

    private async Task<IpcResponse> HandleStage2AnswersAsync(
        string correlationId,
        SubmitSocraticAnswersRequest req,
        CancellationToken ct)
    {
        PendingSubmission? pending;
        lock (_sessionLock)
        {
            pending = _pendingSubmission;
        }

        if (pending is null)
        {
            return IpcResponse.Fail(correlationId, "Önce ödev dosyası yüklemelisiniz.");
        }

        if (string.IsNullOrWhiteSpace(req.Answer1) || string.IsNullOrWhiteSpace(req.Answer2))
        {
            return IpcResponse.Fail(correlationId, "Lütfen her iki doğrulama sorusunu da yanıtlayın.");
        }

        if (!_timeService.IsInternetAvailable)
        {
            return IpcResponse.Fail(correlationId, "Ödev kontrolü için internet bağlantısı bekleniyor.", "NO_INTERNET");
        }

        string apiKey = _db.GetGeminiApiKey();
        var rules = _db.GetRuleConfig();
        string criteria = rules.DailyHomeworkCriteria;
        DateTime localNow = _timeService.GetTrustedLocalNow();
        string dateLocal = localNow.ToString("yyyy-MM-dd");

        try
        {
            var stage2 = await _gemini.EvaluateStage2Async(
                apiKey,
                criteria,
                pending.FileBytes,
                pending.MimeType,
                pending.FileName,
                pending.Stage1Summary,
                pending.Questions,
                req.Answer1,
                req.Answer2,
                ct,
                rules.GeminiModel);

            if (!stage2.Approved)
            {
                lock (_sessionLock)
                {
                    _evalStage = HomeworkEvaluationStage.Rejected;
                    _lastAiFeedback = stage2.Feedback;
                    _lastScore = stage2.Score;
                }

                _db.AddHomeworkAttempt(dateLocal, "Stage2_Rejected", false, stage2.Score, stage2.Feedback);
                _db.AddAuditLog("HOMEWORK_ANSWERS_REJECTED", "Student", $"Puan: {stage2.Score} | C1: {Truncate(req.Answer1, 60)} | C2: {Truncate(req.Answer2, 60)}");

                NotifyTelegram($"""
                    ❌ Sokratik Sorular Yetersiz (Aşama 2 Reddedildi)
                    📊 Puan: {stage2.Score}/100
                    ❓ Soru 1: {pending.Questions.ElementAtOrDefault(0)}
                    ✍️ Öğrenci Cevabı 1: {req.Answer1}
                    ❓ Soru 2: {pending.Questions.ElementAtOrDefault(1)}
                    ✍️ Öğrenci Cevabı 2: {req.Answer2}
                    💬 AI Geri Bildirimi: {stage2.Feedback}
                    """);

                return IpcResponse.Ok(correlationId, stage2.Feedback, GetCurrentStatus());
            }

            // Approved! Persist today's approval and unlock
            _db.SetDateApproved(dateLocal, DateTime.UtcNow, stage2.Score, pending.Stage1Summary, pending.SavedFilePath);
            _db.AddHomeworkAttempt(dateLocal, "Stage2_Approved", true, stage2.Score, stage2.Feedback);
            _db.AddAuditLog("HOMEWORK_APPROVED", "GeminiAI", $"Tarih: {dateLocal}, Puan: {stage2.Score}, Özet: {pending.Stage1Summary}");

            var approvedPending = pending;
            lock (_sessionLock)
            {
                _evalStage = HomeworkEvaluationStage.Approved;
                _lastAiFeedback = stage2.Feedback;
                _lastScore = stage2.Score;
                _pendingSubmission = null;
            }

            if (_telegramNotifier is not null)
            {
                string qaLog = $"""
                    ✅ Ödev AI Tarafından Onaylandı! Bilgisayar kilidi açıldı.
                    📊 Puan: {stage2.Score}/100
                    📝 Özet: {approvedPending.Stage1Summary}
                    ❓ Soru 1: {approvedPending.Questions.ElementAtOrDefault(0)}
                    ✍️ Cevap 1: {req.Answer1}
                    ❓ Soru 2: {approvedPending.Questions.ElementAtOrDefault(1)}
                    ✍️ Cevap 2: {req.Answer2}
                    💬 AI Notu: {stage2.Feedback}
                    """;
                _ = Task.Run(() => _telegramNotifier.SendAdminTextAsync(qaLog));
            }

            return IpcResponse.Ok(correlationId, "Tebrikler! Ödeviniz onaylandı, bilgisayar kilidi açılıyor.", GetCurrentStatus());
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Gemini Stage 2 hatası");
            return IpcResponse.Fail(correlationId, $"AI doğrulama hatası: {ex.Message}", null, GetCurrentStatus());
        }
    }

    public void ResetEvaluationSession()
    {
        lock (_sessionLock)
        {
            _evalStage = HomeworkEvaluationStage.Idle;
            _socraticQuestions = [];
            _lastAiFeedback = string.Empty;
            _lastScore = null;
            _pendingSubmission = null;
        }
    }

    public void ForceUnlockToday(string actor)
    {
        DateTime localNow = _timeService.GetTrustedLocalNow();
        string todayKey = localNow.ToString("yyyy-MM-dd");
        _db.SetDateApproved(todayKey, DateTime.UtcNow, 100, $"Yönetici ({actor}) tarafından acil kilit açma uygulandı.", string.Empty);
        _db.ResetUsedSecondsForDate(todayKey);
        _db.AddAuditLog("FORCE_UNLOCK", actor, $"Tarih: {todayKey} için kilit açıldı (günlük sayaç sıfırlandı).");
        if (!string.Equals(actor, "AdminTelegram", StringComparison.OrdinalIgnoreCase))
        {
            NotifyTelegram($"🔓 Bilgisayar kilidi '{actor}' tarafından bugün için açıldı.");
        }
    }

    public void RecordUnlockedElapsedSeconds(int deltaSeconds)
    {
        if (deltaSeconds <= 0) return;
        string todayKey = _timeService.GetTrustedLocalNow().ToString("yyyy-MM-dd");
        _db.AddUsedSecondsForDate(todayKey, deltaSeconds);
    }

    public void UpdateSchedule(int startHour, int startMinute, int endHour, int endMinute, string actor)
    {
        var rules = _db.GetRuleConfig();
        rules.StartHour = Math.Clamp(startHour, 0, 23);
        rules.StartMinute = Math.Clamp(startMinute, 0, 59);
        rules.EndHour = Math.Clamp(endHour, 0, 23);
        rules.EndMinute = Math.Clamp(endMinute, 0, 59);
        foreach (var dayRule in rules.WeeklySchedule)
        {
            dayRule.StartHour = rules.StartHour;
            dayRule.StartMinute = rules.StartMinute;
            dayRule.EndHour = rules.EndHour;
            dayRule.EndMinute = rules.EndMinute;
        }
        _db.SaveRuleConfig(rules);
        _db.AddAuditLog("SCHEDULE_UPDATED", actor, $"İzinli saat güncellendi (tüm günler): {rules.StartHour:D2}:{rules.StartMinute:D2} - {rules.EndHour:D2}:{rules.EndMinute:D2}");
        if (!string.Equals(actor, "AdminTelegram", StringComparison.OrdinalIgnoreCase))
        {
            NotifyTelegram($"🕒 Kullanım saatleri '{actor}' tarafından güncellendi: {rules.StartHour:D2}:{rules.StartMinute:D2} - {rules.EndHour:D2}:{rules.EndMinute:D2}");
        }
    }

    public void UpdateMaxUsageMinutes(int minutes, string actor)
    {
        var rules = _db.GetRuleConfig();
        rules.DailyMaxUsageMinutes = Math.Max(0, minutes);
        foreach (var dayRule in rules.WeeklySchedule)
        {
            dayRule.MaxUsageMinutes = rules.DailyMaxUsageMinutes;
        }
        _db.SaveRuleConfig(rules);
        _db.AddAuditLog("MAX_USAGE_UPDATED", actor, $"Günlük maksimum süre güncellendi (tüm günler): {rules.DailyMaxUsageMinutes} dk");
        if (!string.Equals(actor, "AdminTelegram", StringComparison.OrdinalIgnoreCase))
        {
            NotifyTelegram($"⏱️ Günlük süre limiti '{actor}' tarafından güncellendi: {(rules.DailyMaxUsageMinutes == 0 ? "Sınırsız" : $"{rules.DailyMaxUsageMinutes} dk")}");
        }
    }

    public void UpdateDaySchedule(
        DayOfWeek day,
        int? startHour,
        int? startMinute,
        int? endHour,
        int? endMinute,
        int maxUsageMinutes,
        bool isDayBlocked,
        string actor)
    {
        var rules = _db.GetRuleConfig();
        var rule = rules.WeeklySchedule.FirstOrDefault(r => r.Day == day);
        if (rule is null)
        {
            rule = new DailyScheduleRule { Day = day, StartHour = rules.StartHour, StartMinute = rules.StartMinute, EndHour = rules.EndHour, EndMinute = rules.EndMinute };
            rules.WeeklySchedule.Add(rule);
        }

        if (startHour.HasValue) rule.StartHour = Math.Clamp(startHour.Value, 0, 23);
        if (startMinute.HasValue) rule.StartMinute = Math.Clamp(startMinute.Value, 0, 59);
        if (endHour.HasValue) rule.EndHour = Math.Clamp(endHour.Value, 0, 23);
        if (endMinute.HasValue) rule.EndMinute = Math.Clamp(endMinute.Value, 0, 59);
        rule.MaxUsageMinutes = Math.Max(0, maxUsageMinutes);
        rule.IsDayBlocked = isDayBlocked;

        _db.SaveRuleConfig(rules);
        string dayTr = HomeworkRuleConfig.GetTurkishDayName(day);
        string summary = isDayBlocked
            ? $"{dayTr}: Kapalı"
            : $"{dayTr}: {rule.StartHour:D2}:{rule.StartMinute:D2}-{rule.EndHour:D2}:{rule.EndMinute:D2}, {(rule.MaxUsageMinutes == 0 ? "Sınırsız" : $"{rule.MaxUsageMinutes} dk")}";
        _db.AddAuditLog("DAY_SCHEDULE_UPDATED", actor, summary);
    }

    public (bool Changed, string Message) ModifyWhitelistSite(string rawUrl, bool add, string actor)
    {
        string normalized = rawUrl.Trim();
        if (add && !normalized.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !normalized.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "https://" + normalized;
        }

        var rules = _db.GetRuleConfig();
        if (add)
        {
            if (!Uri.TryCreate(normalized, UriKind.Absolute, out _))
            {
                return (false, "Geçersiz URL biçimi.");
            }
            if (rules.WhitelistUrls.Any(u => string.Equals(u.TrimEnd('/'), normalized.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)))
            {
                return (false, $"Site zaten listede: {normalized}");
            }
            rules.WhitelistUrls.Add(normalized);
            _db.SaveRuleConfig(rules);
            _db.AddAuditLog("WHITELIST_SITE_ADDED", actor, normalized);
            return (true, $"İzin listesine eklendi: {normalized}");
        }

        int removed = rules.WhitelistUrls.RemoveAll(u => u.Contains(normalized, StringComparison.OrdinalIgnoreCase));
        if (removed == 0)
        {
            return (false, $"Eşleşen site bulunamadı: {rawUrl}");
        }
        _db.SaveRuleConfig(rules);
        _db.AddAuditLog("WHITELIST_SITE_REMOVED", actor, rawUrl);
        return (true, $"{removed} site izin listesinden kaldırıldı.");
    }

    public void ForceLockNow(string actor)
    {
        DateTime localNow = _timeService.GetTrustedLocalNow();
        string todayKey = localNow.ToString("yyyy-MM-dd");
        _db.RevokeDateApproval(todayKey);
        ResetEvaluationSession();
        _db.AddAuditLog("FORCE_LOCK", actor, $"Tarih: {todayKey} için kilit zorla kapatıldı.");
        if (!string.Equals(actor, "AdminTelegram", StringComparison.OrdinalIgnoreCase))
        {
            NotifyTelegram($"🔒 Bilgisayar '{actor}' tarafından kilitlendi.");
        }
    }

    public void EnterMaintenanceMode(string actor)
    {
        lock (_sessionLock)
        {
            _isMaintenanceMode = true;
        }
        _tamperProtection?.SuspendForMaintenance();
        _db.AddAuditLog("MAINTENANCE_MODE_ON", actor, "Bakım modu açıldı. Watchdog ve dosya/OS kısıtlamaları geçici olarak askıya alındı.");
        if (!string.Equals(actor, "AdminTelegram", StringComparison.OrdinalIgnoreCase))
        {
            NotifyTelegram($"🛠️ Tam Yönetici (Bakım) Modu '{actor}' tarafından AÇILDI. Watchdog ve OS kısıtları geçici olarak durduruldu.");
        }
    }

    public void ExitMaintenanceMode(string actor)
    {
        lock (_sessionLock)
        {
            _isMaintenanceMode = false;
        }
        _tamperProtection?.EnforceProtection();
        _db.AddAuditLog("MAINTENANCE_MODE_OFF", actor, "Bakım modu kapatıldı, Watchdog ve koruma kilitleri tekrar devrede.");
        if (!string.Equals(actor, "AdminTelegram", StringComparison.OrdinalIgnoreCase))
        {
            NotifyTelegram($"🛡️ Tam Yönetici (Bakım) Modu '{actor}' tarafından KAPATILDI. Kiosk ve koruma kilitleri tekrar devrede.");
        }
    }

    public void RecordStudentActivityEvent(string eventType, string details)
    {
        string type = eventType.Trim().ToUpperInvariant();
        string cleanDetails = details?.Trim() ?? string.Empty;

        // Throttle identical browser navigation logs for 30s so parent Telegram stays clean
        if (type is "BROWSER_NAV_ALLOWED" or "BROWSER_NAV_BLOCKED")
        {
            string sig = $"{type}|{cleanDetails}";
            lock (_sessionLock)
            {
                if (sig == _lastBrowserEventSignature && (DateTime.UtcNow - _lastBrowserEventUtc) < TimeSpan.FromSeconds(30))
                {
                    return;
                }
                _lastBrowserEventSignature = sig;
                _lastBrowserEventUtc = DateTime.UtcNow;
            }
        }

        _db.AddAuditLog(type, "StudentUI", cleanDetails);

        string icon = type switch
        {
            "BROWSER_NAV_BLOCKED" => "🚫 İzin Dışı Site Engellendi",
            "BROWSER_NAV_ALLOWED" => "🌐 Çalışma Tarayıcısı Ziyareti",
            "WEBCAM_CAPTURED" => "📷 Kamera İle Fotoğraf Çekildi",
            "FILE_SELECTED" => "📎 Ödev Dosyası Seçildi",
            _ => $"📌 Öğrenci Etkinliği ({type})"
        };

        NotifyTelegram($"{icon}\n{cleanDetails}");
    }

    public void NotifyTelegram(string message)
    {
        if (_telegramNotifier is null || string.IsNullOrWhiteSpace(message)) return;
        _ = Task.Run(() => _telegramNotifier.SendAdminTextAsync(message));
    }

    private void NotifyTelegramMedia(string filePath, string fileName, string caption)
    {
        if (_telegramNotifier is null) return;
        _ = Task.Run(() => _telegramNotifier.SendSubmissionMediaLogAsync(filePath, fileName, caption));
    }

    public string BuildWeeklyScheduleReport()
    {
        var rules = _db.GetRuleConfig();
        var sb = new StringBuilder();
        sb.AppendLine("📅 *Haftalık Süre & Saat Planı*");
        foreach (var d in rules.WeeklySchedule)
        {
            string dayName = HomeworkRuleConfig.GetTurkishDayName(d.Day);
            if (d.IsDayBlocked)
            {
                sb.AppendLine($"• *{dayName}:* 🚫 Kapalı");
            }
            else
            {
                string lim = d.MaxUsageMinutes == 0 ? "Sınırsız" : $"{d.MaxUsageMinutes} dk";
                sb.AppendLine($"• *{dayName}:* `{d.StartHour:D2}:{d.StartMinute:D2} - {d.EndHour:D2}:{d.EndMinute:D2}` | Limit: `{lim}`");
            }
        }
        return sb.ToString();
    }

    public string BuildWhitelistReport()
    {
        var rules = _db.GetRuleConfig();
        var sb = new StringBuilder();
        sb.AppendLine($"🌐 *Eğitim Tarayıcısı İzin Listesi ({rules.WhitelistUrls.Count} site)*");
        for (int i = 0; i < rules.WhitelistUrls.Count; i++)
        {
            sb.AppendLine($"{i + 1}. {rules.WhitelistUrls[i]}");
        }
        return sb.ToString();
    }

    public string BuildRecentLogsReport(int count = 12)
    {
        var logs = _db.GetRecentAuditLogs(Math.Clamp(count, 1, 25));
        var sb = new StringBuilder();
        sb.AppendLine($"📜 *Son {logs.Count} Sistem & Öğrenci Kaydı*");
        foreach (var l in logs)
        {
            sb.AppendLine($"• `[{l.TimestampUtc.ToLocalTime():dd.MM HH:mm}]` *{l.EventType}* ({l.Actor}): {Truncate(l.Details, 95)}");
        }
        return sb.ToString();
    }

    public string BuildTelegramStatusReport()
    {
        var status = GetCurrentStatus();
        var attempts = _db.GetRecentAttempts(5);
        var sb = new StringBuilder();
        sb.AppendLine("📊 *HomeworkGuard Durum Raporu*");
        sb.AppendLine($"• Gün: *{status.EffectiveDayName}* | Durum: `{status.CurrentState}`");
        sb.AppendLine($"• Bakım Modu: {(status.IsMaintenanceMode ? "🛠️ Açık" : "🛡️ Kapalı (Koruma Aktif)")}");
        sb.AppendLine($"• İzinli Pencere: `{status.StartHour:D2}:{status.StartMinute:D2} - {status.EndHour:D2}:{status.EndMinute:D2}` | Limit: `{(status.DailyMaxUsageMinutes == 0 ? "Sınırsız" : $"{status.UsedMinutesToday}/{status.DailyMaxUsageMinutes} dk")}`");
        sb.AppendLine($"• Güvenilir Saat: `{status.EffectiveLocalNow:yyyy-MM-dd HH:mm:ss}` (NTP: {(status.IsTimeSyncedWithNtp ? "Evet" : "Hayır")})");
        sb.AppendLine($"• İnternet: {(status.IsInternetAvailable ? "Bağlı" : "Yok")}");
        sb.AppendLine($"• Bugünkü Onay: {(status.IsTodayApproved ? $"Onaylandı ({status.ApprovedDate})" : "Henüz Onaylanmadı")}");
        sb.AppendLine($"• Kalan Süre: {status.RemainingFreeTimeText}");
        sb.AppendLine($"• Günlük Ödev Kriteri: {status.DailyCriteria}");
        if (attempts.Count > 0)
        {
            sb.AppendLine("\n📝 *Son AI Denemeleri:*");
            foreach (var a in attempts)
            {
                string sc = a.Score.HasValue ? $" (Puan: {a.Score})" : "";
                sb.AppendLine($"- [{a.TimestampUtc.ToLocalTime():HH:mm}] {a.Stage}{sc}: {Truncate(a.Summary, 90)}");
            }
        }
        return sb.ToString();
    }

    private AdminSettingsDto BuildAdminSettingsDto()
    {
        var rules = _db.GetRuleConfig();
        return new AdminSettingsDto
        {
            StartHour = rules.StartHour,
            StartMinute = rules.StartMinute,
            EndHour = rules.EndHour,
            EndMinute = rules.EndMinute,
            WarningMinutesBeforeLock = rules.WarningMinutesBeforeLock,
            DailyMaxUsageMinutes = rules.DailyMaxUsageMinutes,
            WeeklySchedule = rules.WeeklySchedule,
            GeminiModel = rules.GeminiModel,
            DailyCriteria = rules.DailyHomeworkCriteria,
            WhitelistUrls = rules.WhitelistUrls,
            HasGeminiApiKey = !string.IsNullOrWhiteSpace(_db.GetGeminiApiKey()),
            HasTelegramBotToken = !string.IsNullOrWhiteSpace(_db.GetTelegramBotToken()),
            AdminChatId = _db.GetAdminChatId()?.ToString() ?? string.Empty,
            RecentAuditLogs = _db.GetRecentAuditLogs(50),
            RecentAttempts = _db.GetRecentAttempts(20)
        };
    }

    private void ApplyAdminSettingsUpdate(AdminUpdateSettingsRequest req)
    {
        var weekly = req.WeeklySchedule is { Count: > 0 }
            ? req.WeeklySchedule
            : HomeworkRuleConfig.CreateDefaultWeeklySchedule(req.StartHour, req.StartMinute, req.EndHour, req.EndMinute, req.DailyMaxUsageMinutes);

        var rules = new HomeworkRuleConfig
        {
            StartHour = req.StartHour,
            StartMinute = req.StartMinute,
            EndHour = req.EndHour,
            EndMinute = req.EndMinute,
            WarningMinutesBeforeLock = req.WarningMinutesBeforeLock,
            DailyMaxUsageMinutes = req.DailyMaxUsageMinutes,
            WeeklySchedule = weekly,
            GeminiModel = string.IsNullOrWhiteSpace(req.GeminiModel) ? AppBuildConfig.GeminiModelName : req.GeminiModel.Trim(),
            DailyHomeworkCriteria = req.DailyCriteria,
            WhitelistUrls = req.WhitelistUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Select(u => u.Trim()).ToList()
        };
        _db.SaveRuleConfig(rules);

        if (!string.IsNullOrWhiteSpace(req.NewGeminiApiKey))
            _db.SetGeminiApiKey(req.NewGeminiApiKey);

        if (!string.IsNullOrWhiteSpace(req.NewTelegramBotToken))
            _db.SetTelegramBotToken(req.NewTelegramBotToken);

        if (!string.IsNullOrWhiteSpace(req.AdminChatId))
            _db.SetAdminChatId(req.AdminChatId);

        if (!string.IsNullOrWhiteSpace(req.NewAdminPassword))
            _db.SetAdminPassword(req.NewAdminPassword);

        _db.AddAuditLog("SETTINGS_UPDATED", "AdminConsole", "Yönetici ayarları güncellendi.");
        NotifyTelegram("⚙️ HomeworkGuard ayarları (zamanlama / haftalık plan / kriterler) Yönetici Konsolu'ndan güncellendi.");
    }

    private static T? DeserializePayload<T>(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return default;
        return JsonSerializer.Deserialize<T>(json, IpcJson.Options);
    }

    private static string Truncate(string text, int maxLen) =>
        text.Length <= maxLen ? text : text[..maxLen] + "...";
}
