using System.Windows;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.Core.Models;
using HomeworkGuard.UI.Services;

namespace HomeworkGuard.UI;

public partial class AdminConsoleWindow : Window
{
    public sealed class WeeklyRowViewModel
    {
        public DayOfWeek Day { get; set; }
        public string DayName { get; set; } = string.Empty;
        public string StartTimeText { get; set; } = "10:00";
        public string EndTimeText { get; set; } = "22:00";
        public string MaxMinutesText { get; set; } = "0";
        public bool IsBlocked { get; set; }
    }

    private readonly NamedPipeIpcClient _ipc;
    private readonly AdminAuthResult _auth;
    private List<WeeklyRowViewModel> _weeklyRows = [];

    public bool SafeExitRequested { get; private set; }
    public bool StatusChanged { get; private set; }
    public string AdminSessionToken => _auth.SessionToken;

    public AdminConsoleWindow(NamedPipeIpcClient ipc, AdminAuthResult auth)
    {
        _ipc = ipc;
        _auth = auth;
        InitializeComponent();

        AuthMethodText.Text = $"Doğrulama Yöntemi: {auth.AuthMethod}";
        if (auth.RequiresPasswordReset)
        {
            RecoveryWarningBanner.Visibility = Visibility.Visible;
        }

        Loaded += async (_, _) => await LoadSettingsAsync();
    }

    private async Task LoadSettingsAsync()
    {
        var resp = await _ipc.SendAsync(IpcCommands.AdminGetSettings, new AdminSessionRequest { SessionToken = _auth.SessionToken });
        if (!resp.Success)
        {
            StatusFooterText.Text = resp.Message;
            return;
        }

        var dto = NamedPipeIpcClient.Unpack<AdminSettingsDto>(resp);
        if (dto is null) return;

        StartTimeBox.Text = $"{dto.StartHour:D2}:{dto.StartMinute:D2}";
        EndTimeBox.Text = $"{dto.EndHour:D2}:{dto.EndMinute:D2}";
        MaxUsageMinutesBox.Text = dto.DailyMaxUsageMinutes.ToString();
        WarningMinutesBox.Text = dto.WarningMinutesBeforeLock.ToString();
        GeminiModelBox.Text = dto.GeminiModel;
        CriteriaBox.Text = dto.DailyCriteria;
        WhitelistBox.Text = string.Join(Environment.NewLine, dto.WhitelistUrls);
        AdminChatIdBox.Text = dto.AdminChatId;
        GeminiStatusLabel.Text = dto.HasGeminiApiKey
            ? "Gemini API Key (Kayıtlı — değiştirmek için yenisini girin)"
            : "Gemini API Key (Henüz tanımlanmadı)";
        TelegramStatusLabel.Text = dto.HasTelegramBotToken
            ? "Telegram Bot Token (Kayıtlı — değiştirmek için yenisini girin)"
            : "Telegram Bot Token (Henüz tanımlanmadı)";

        AuditGrid.ItemsSource = dto.RecentAuditLogs;
        AttemptsGrid.ItemsSource = dto.RecentAttempts;

        var sourceSchedule = dto.WeeklySchedule is { Count: > 0 }
            ? dto.WeeklySchedule
            : HomeworkRuleConfig.CreateDefaultWeeklySchedule(dto.StartHour, dto.StartMinute, dto.EndHour, dto.EndMinute, dto.DailyMaxUsageMinutes);

        _weeklyRows = sourceSchedule.Select(r => new WeeklyRowViewModel
        {
            Day = r.Day,
            DayName = HomeworkRuleConfig.GetTurkishDayName(r.Day),
            StartTimeText = $"{r.StartHour:D2}:{r.StartMinute:D2}",
            EndTimeText = $"{r.EndHour:D2}:{r.EndMinute:D2}",
            MaxMinutesText = r.MaxUsageMinutes.ToString(),
            IsBlocked = r.IsDayBlocked
        }).ToList();
        WeeklyScheduleItems.ItemsSource = _weeklyRows;
    }

    private async void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseHourMinute(StartTimeBox.Text, out int sh, out int sm) ||
            !TryParseHourMinute(EndTimeBox.Text, out int eh, out int em))
        {
            StatusFooterText.Text = "Saat formatı HH:mm olmalıdır (örn: 10:00 ve 22:00).";
            return;
        }

        if (!int.TryParse(MaxUsageMinutesBox.Text.Trim(), out int maxMinutes) || maxMinutes < 0)
        {
            StatusFooterText.Text = "Günlük toplam süre 0 veya pozitif bir tam sayı olmalıdır (dakika).";
            return;
        }

        if (!int.TryParse(WarningMinutesBox.Text.Trim(), out int warningMinutes) || warningMinutes < 1 || warningMinutes > 60)
        {
            StatusFooterText.Text = "Uyarı süresi 1 ile 60 dakika arasında olmalıdır.";
            return;
        }

        var urls = WhitelistBox.Text
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        var weeklyRules = new List<DailyScheduleRule>();
        foreach (var row in _weeklyRows)
        {
            if (!TryParseHourMinute(row.StartTimeText, out int rsh, out int rsm) ||
                !TryParseHourMinute(row.EndTimeText, out int reh, out int rem))
            {
                StatusFooterText.Text = $"{row.DayName} için saat formatı HH:mm olmalıdır.";
                return;
            }
            if (!int.TryParse(row.MaxMinutesText.Trim(), out int rMax) || rMax < 0)
            {
                StatusFooterText.Text = $"{row.DayName} için süre 0 veya pozitif sayı olmalıdır.";
                return;
            }
            weeklyRules.Add(new DailyScheduleRule
            {
                Day = row.Day,
                StartHour = rsh,
                StartMinute = rsm,
                EndHour = reh,
                EndMinute = rem,
                MaxUsageMinutes = rMax,
                IsDayBlocked = row.IsBlocked
            });
        }

        var req = new AdminUpdateSettingsRequest
        {
            SessionToken = _auth.SessionToken,
            StartHour = sh,
            StartMinute = sm,
            EndHour = eh,
            EndMinute = em,
            DailyMaxUsageMinutes = maxMinutes,
            WarningMinutesBeforeLock = warningMinutes,
            WeeklySchedule = weeklyRules,
            GeminiModel = string.IsNullOrWhiteSpace(GeminiModelBox.Text) ? AppBuildConfig.GeminiModelName : GeminiModelBox.Text.Trim(),
            DailyCriteria = CriteriaBox.Text.Trim(),
            WhitelistUrls = urls,
            NewGeminiApiKey = string.IsNullOrWhiteSpace(GeminiKeyBox.Password) ? null : GeminiKeyBox.Password.Trim(),
            NewTelegramBotToken = string.IsNullOrWhiteSpace(TelegramTokenBox.Password) ? null : TelegramTokenBox.Password.Trim(),
            AdminChatId = AdminChatIdBox.Text.Trim(),
            NewAdminPassword = string.IsNullOrWhiteSpace(NewPasswordBox.Password) ? null : NewPasswordBox.Password
        };

        var resp = await _ipc.SendAsync(IpcCommands.AdminUpdateSettings, req);
        StatusFooterText.Text = resp.Message;
        if (resp.Success)
        {
            GeminiKeyBox.Clear();
            TelegramTokenBox.Clear();
            NewPasswordBox.Clear();
            RecoveryWarningBanner.Visibility = Visibility.Collapsed;
            StatusChanged = true;
            await LoadSettingsAsync();
        }
    }

    private async void ForceUnlock_Click(object sender, RoutedEventArgs e)
    {
        var resp = await _ipc.SendAsync(IpcCommands.AdminForceUnlock, new AdminSessionRequest { SessionToken = _auth.SessionToken });
        StatusFooterText.Text = resp.Message;
        if (resp.Success)
        {
            StatusChanged = true;
            Close();
        }
    }

    private async void ForceLock_Click(object sender, RoutedEventArgs e)
    {
        var resp = await _ipc.SendAsync(IpcCommands.AdminForceLock, new AdminSessionRequest { SessionToken = _auth.SessionToken });
        StatusFooterText.Text = resp.Message;
        if (resp.Success)
        {
            StatusChanged = true;
            await LoadSettingsAsync();
        }
    }

    private async void SafeExit_Click(object sender, RoutedEventArgs e)
    {
        var resp = await _ipc.SendAsync(IpcCommands.AdminEnterMaintenanceMode, new AdminSessionRequest { SessionToken = _auth.SessionToken });
        if (resp.Success)
        {
            SafeExitRequested = true;
            StatusChanged = true;
            Close();
        }
        else
        {
            StatusFooterText.Text = resp.Message;
        }
    }

    private async void RotateRecoveryKey_Click(object sender, RoutedEventArgs e)
    {
        var resp = await _ipc.SendAsync(IpcCommands.AdminRotateRecoveryKey, new AdminSessionRequest { SessionToken = _auth.SessionToken });
        if (resp.Success)
        {
            var data = NamedPipeIpcClient.Unpack<AdminRotateRecoveryKeyResponse>(resp);
            if (data is not null)
            {
                NewRecoveryKeyOutput.Text = $"YENİ MASTER RECOVERY KEY (GÜVENLİ YERE NOT EDİN): {data.NewRecoveryKey}";
                NewRecoveryKeyOutput.Visibility = Visibility.Visible;
            }
        }
        StatusFooterText.Text = resp.Message;
    }

    private void LoadDefaultStudySites_Click(object sender, RoutedEventArgs e)
    {
        WhitelistBox.Text = string.Join(Environment.NewLine, StudyWhitelistCatalog.GetDefaultUrls());
        StatusFooterText.Text = "Varsayılan 12 Türkçe ders sitesi listeye eklendi. Kaydetmek için 'Ayarları Kaydet' butonuna basın.";
    }

    private async void ResumeProtection_Click(object sender, RoutedEventArgs e)
    {
        var resp = await _ipc.SendAsync(IpcCommands.AdminExitMaintenanceMode, new AdminSessionRequest { SessionToken = _auth.SessionToken });
        StatusFooterText.Text = resp.Message;
        if (resp.Success)
        {
            StatusChanged = true;
            await LoadSettingsAsync();
        }
    }

    private void ApplyDefaultToAllDays_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseHourMinute(StartTimeBox.Text, out int sh, out int sm) ||
            !TryParseHourMinute(EndTimeBox.Text, out int eh, out int em) ||
            !int.TryParse(MaxUsageMinutesBox.Text.Trim(), out int mins) || mins < 0)
        {
            StatusFooterText.Text = "Önce üstteki genel Açılış, Gece Kilidi ve Günlük Süre değerlerini geçerli biçimde girin.";
            return;
        }

        foreach (var row in _weeklyRows)
        {
            row.StartTimeText = $"{sh:D2}:{sm:D2}";
            row.EndTimeText = $"{eh:D2}:{em:D2}";
            row.MaxMinutesText = mins.ToString();
            row.IsBlocked = false;
        }
        WeeklyScheduleItems.Items.Refresh();
        StatusFooterText.Text = "Genel kural tüm haftanın 7 gününe uygulandı. Kalıcı olması için 'Ayarları Kaydet' butonuna basın.";
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static bool TryParseHourMinute(string input, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        var parts = input.Trim().Split(':');
        if (parts.Length != 2) return false;
        return int.TryParse(parts[0], out hour) && int.TryParse(parts[1], out minute) &&
               hour >= 0 && hour <= 23 && minute >= 0 && minute <= 59;
    }
}
