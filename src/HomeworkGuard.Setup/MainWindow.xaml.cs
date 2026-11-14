using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.Core.Models;

namespace HomeworkGuard.Setup;

public partial class MainWindow : Window
{
    public sealed class SetupWeeklyRow
    {
        public DayOfWeek Day { get; set; }
        public string DayName { get; set; } = string.Empty;
        public string StartTimeText { get; set; } = "10:00";
        public string EndTimeText { get; set; } = "22:00";
        public string MaxMinutesText { get; set; } = "0";
        public bool IsBlocked { get; set; }
    }

    private bool _isVmEnvironment;
    private bool _isBusy;
    private string? _activeAdminSessionToken;
    private List<SetupWeeklyRow> _weeklyRows = [];

    public MainWindow()
    {
        InitializeComponent();
        InitWeeklyRows();
        DetectEnvironment();
        Loaded += (_, _) => PlayEntranceAnimation();
        Closed += MainWindow_Closed;
    }

    private void InitWeeklyRows()
    {
        _weeklyRows = HomeworkRuleConfig.CreateDefaultWeeklySchedule()
            .Select(d => new SetupWeeklyRow
            {
                Day = d.Day,
                DayName = HomeworkRuleConfig.GetTurkishDayName(d.Day),
                StartTimeText = $"{d.StartHour:D2}:{d.StartMinute:D2}",
                EndTimeText = $"{d.EndHour:D2}:{d.EndMinute:D2}",
                MaxMinutesText = d.MaxUsageMinutes.ToString(),
                IsBlocked = d.IsDayBlocked
            }).ToList();
        SetupWeeklyItems.ItemsSource = _weeklyRows;
    }

    private void SyncWeeklyFromGeneral_Click(object sender, RoutedEventArgs e)
    {
        if (!TryParseTime(StartTimeBox.Text, out int sh, out int sm) ||
            !TryParseTime(EndTimeBox.Text, out int eh, out int em) ||
            !int.TryParse(MaxUsageBox.Text.Trim(), out int mins) || mins < 0)
        {
            MessageBox.Show("Önce geçerli Açılış, Gece Kilidi ve Günlük Süre değerleri girin.", "Geçersiz Değer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        foreach (var row in _weeklyRows)
        {
            row.StartTimeText = $"{sh:D2}:{sm:D2}";
            row.EndTimeText = $"{eh:D2}:{em:D2}";
            row.MaxMinutesText = mins.ToString();
            row.IsBlocked = false;
        }
        SetupWeeklyItems.Items.Refresh();
    }

    private void PlayEntranceAnimation()
    {
        var sb = new Storyboard();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, RootContainer);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);

        var slide = new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, RootContainer);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        sb.Children.Add(slide);
        sb.Begin();
    }

    private void DetectEnvironment()
    {
        string model = GetComputerModel();
        _isVmEnvironment = model.Contains("VirtualBox", StringComparison.OrdinalIgnoreCase) ||
                           model.Contains("VMware", StringComparison.OrdinalIgnoreCase) ||
                           model.Contains("KVM", StringComparison.OrdinalIgnoreCase) ||
                           model.Contains("QEMU", StringComparison.OrdinalIgnoreCase) ||
                           model.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase) ||
                           model.Contains("Bochs", StringComparison.OrdinalIgnoreCase);

        if (_isVmEnvironment)
        {
            EnvBadge.Background = new SolidColorBrush(Color.FromRgb(14, 36, 28));
            EnvBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            EnvDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            EnvBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(167, 243, 208));
            EnvBadgeText.Text = $"Sanal Makine • {model}";
            HostWarningBanner.Visibility = Visibility.Collapsed;
        }
        else
        {
            EnvBadge.Background = new SolidColorBrush(Color.FromRgb(40, 21, 27));
            EnvBadge.BorderBrush = new SolidColorBrush(Color.FromRgb(225, 29, 72));
            EnvDot.Fill = new SolidColorBrush(Color.FromRgb(244, 63, 94));
            EnvBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(253, 164, 175));
            EnvBadgeText.Text = $"Fiziksel PC • {model}";
            HostWarningBanner.Visibility = Visibility.Visible;
        }

        UpdateButtonStates();
    }

    private void Help_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string key) return;

        (string title, string body) = key switch
        {
            "admin_pass" => (
                "Yönetici Şifresi Nasıl Belirlenir?",
                "Kilit ekranı aktifken 'Ctrl + Alt + Shift + F12' tuşlarıyla (veya Başlat Menüsündeki 'HomeworkGuard Yönetici' kısayoluyla) ayar panelini açmak için kullanılır. En az 6 karakterli, tahmin edilmesi zor bir şifre girin."),
            "gemini_key" => (
                "Gemini API Key Nereden Alınır?",
                "1) Tarayıcıdan https://aistudio.google.com/apikey adresine gidin ve Google hesabınızla giriş yapın.\n2) 'Create API key' butonuna tıklayın.\n3) Üretilen 'AIza...' veya 'AQ...' ile başlayan anahtarı kopyalayıp buraya yapıştırın."),
            "tg_token" => (
                "Telegram Bot Token Nereden Alınır?",
                "1) Telegram'da arama kısmına @BotFather yazıp resmi botu açın.\n2) /newbot komutunu gönderin, botunuza bir isim (ör. Ev Koruması) ve 'bot' ile biten kullanıcı adı verin.\n3) BotFather'ın verdiği '123456:ABC...' token kodunu buraya yapıştırın. Kurulumdan sonra kendi botunuza /start mesajı atmayı unutmayın."),
            "tg_chat" => (
                "Telegram Admin Chat ID Nasıl Öğrenilir?",
                "1) Telegram'da @userinfobot veya @raw_data_bot hesabını açıp 'Start' deyin.\n2) Botun döndürdüğü sayısal 'Id' numarasını (ör. 123456789) kopyalayıp buraya yazın.\nGüvenlik için yalnızca bu ID üzerinden gönderilen komutlar kabul edilir."),
            "schedule" => (
                "Saat Kuralları ve Günlük Süre Limiti",
                "Açılış ve Gece Kilidi dışındaki saatlerde ödev yapılmış olsa bile bilgisayar kilitli kalır. Günlük Süre '0' bırakılırsa kapanış saatine kadar sınırsız erişim verilir; örneğin '120' yazarsanız ödev onaylandıktan sonra toplam 2 saat kullanım hakkı tanınır."),
            "criteria" => (
                "Günlük Ödev Kriteri",
                "Öğrenci ödev fotoğrafı yüklediğinde yapay zekanın arayacağı kuralları buraya yazın (ör. 'En az 15 matematik sorusu ve işlem adımları'). Kurulumdan sonra Yönetici Panelinden veya Telegram '/gorev_ekle' komutuyla dilediğiniz zaman değiştirebilirsiniz."),
            _ => ("Yardım", "Alan hakkında bilgi için fareyi butonun üzerine de getirebilirsiniz.")
        };

        HelpTitleText.Text = title;
        HelpBodyText.Text = body;
        HelpDrawer.Opacity = 0;
        HelpDrawer.Visibility = Visibility.Visible;
        HelpDrawer.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
    }

    private void CloseHelp_Click(object sender, RoutedEventArgs e)
    {
        HelpDrawer.Visibility = Visibility.Collapsed;
    }

    private void SafetyCheck_Changed(object sender, RoutedEventArgs e) => UpdateButtonStates();

    private void UpdateButtonStates()
    {
        bool safetyOk = _isVmEnvironment || ConfirmHostInstallCheck.IsChecked == true;
        InstallBtn.IsEnabled = !_isBusy && safetyOk;
        UninstallBtn.IsEnabled = !_isBusy && safetyOk;
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        string password = AdminPassBox.Password;
        string confirm = AdminPassConfirmBox.Password;
        if (password.Length < 6)
        {
            MessageBox.Show("Yönetici şifresi en az 6 karakter olmalıdır.", "Geçersiz Şifre", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!string.Equals(password, confirm, StringComparison.Ordinal))
        {
            MessageBox.Show("Girilen yönetici şifreleri uyuşmuyor.", "Şifre Hatası", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!TryParseTime(StartTimeBox.Text, out int startH, out int startM) ||
            !TryParseTime(EndTimeBox.Text, out int endH, out int endM))
        {
            MessageBox.Show("Saat biçimi SS:dd olmalıdır (örnek: 10:00 ve 22:00).", "Geçersiz Saat", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(MaxUsageBox.Text.Trim(), out int maxMinutes) || maxMinutes < 0)
        {
            MessageBox.Show("Günlük süre 0 veya pozitif tam sayı olmalıdır.", "Geçersiz Süre", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(WarningMinBox.Text.Trim(), out int warnMinutes) || warnMinutes < 0)
        {
            MessageBox.Show("Uyarı dakikası 0 veya pozitif tam sayı olmalıdır.", "Geçersiz Değer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        string setupDir = AppContext.BaseDirectory;
        string installScript = Path.Combine(setupDir, "Install-Hardening.ps1");
        if (!File.Exists(installScript))
        {
            MessageBox.Show($"Kurulum betiği bulunamadı:\n{installScript}", "Eksik Dosya", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetBusy(true);
        LogBox.Clear();
        RecoveryResultCard.Visibility = Visibility.Collapsed;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = setupDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(installScript);
            psi.ArgumentList.Add("-AdminPassword");
            psi.ArgumentList.Add(password);
            psi.ArgumentList.Add("-ConfirmVmHardening");

            string geminiKey = GeminiKeyBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(geminiKey))
            {
                psi.ArgumentList.Add("-GeminiApiKey");
                psi.ArgumentList.Add(geminiKey);
            }

            string tgToken = TelegramTokenBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(tgToken))
            {
                psi.ArgumentList.Add("-TelegramBotToken");
                psi.ArgumentList.Add(tgToken);
            }

            string chatId = AdminChatIdBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(chatId))
            {
                psi.ArgumentList.Add("-AdminChatId");
                psi.ArgumentList.Add(chatId);
            }

            string criteria = CriteriaBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(criteria))
            {
                psi.ArgumentList.Add("-DailyCriteria");
                psi.ArgumentList.Add(criteria);
            }

            string? recoveredKey = null;
            int exitCode = await RunProcessStreamingAsync(psi, line =>
            {
                if (line.StartsWith("MASTER_RECOVERY_KEY=", StringComparison.OrdinalIgnoreCase))
                {
                    recoveredKey = line["MASTER_RECOVERY_KEY=".Length..].Trim();
                }
            });

            if (exitCode != 0)
            {
                MessageBox.Show("Kurulum sırasında hata oluştu. Lütfen günlük çıktısını inceleyin.", "Kurulum Başarısız", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            await ApplyInitialScheduleViaPipeAsync(password, startH, startM, endH, endM, maxMinutes, warnMinutes, criteria);

            if (!string.IsNullOrWhiteSpace(recoveredKey))
            {
                RecoveryKeyBox.Text = recoveredKey;
                RecoveryResultCard.Opacity = 0;
                RecoveryResultCard.Visibility = Visibility.Visible;
                RecoveryResultCard.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)));
                Topmost = true;
            }

            CloseBtn.Content = "Kiosk Ekranına Geç (Kapat)";
            AppendLog("Kurulum tamamlandı. Recovery Key'i kaydettikten sonra pencereyi kapatarak Kiosk kilidini başlatabilirsiniz.");
        }
        catch (Exception ex)
        {
            AppendLog($"HATA: {ex.Message}");
            MessageBox.Show(ex.Message, "Kurulum Hatası", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void Uninstall_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(
                "HomeworkGuard servisi ve işletim sistemi sıkılaştırma politikaları kaldırılacak. Devam edilsin mi?",
                "Kaldırma Onayı", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        string uninstallScript = Path.Combine(AppContext.BaseDirectory, "Uninstall-Hardening.ps1");
        if (!File.Exists(uninstallScript))
        {
            MessageBox.Show($"Kaldırma betiği bulunamadı:\n{uninstallScript}", "Eksik Dosya", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        SetBusy(true);
        LogBox.Clear();
        RecoveryResultCard.Visibility = Visibility.Collapsed;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                WorkingDirectory = AppContext.BaseDirectory,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("-NoProfile");
            psi.ArgumentList.Add("-ExecutionPolicy");
            psi.ArgumentList.Add("Bypass");
            psi.ArgumentList.Add("-File");
            psi.ArgumentList.Add(uninstallScript);

            int code = await RunProcessStreamingAsync(psi);
            if (code == 0)
            {
                MessageBox.Show("HomeworkGuard başarıyla kaldırıldı.", "Tamamlandı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        finally
        {
            SetBusy(false);
        }
    }

    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(RecoveryKeyBox.Text))
        {
            Clipboard.SetText(RecoveryKeyBox.Text);
            CopyKeyBtn.Content = "✓ Kopyalandı";
        }
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_activeAdminSessionToken))
        {
            await ExitSetupMaintenanceAsync();
        }
        Close();
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_activeAdminSessionToken))
        {
            // Fire-and-wait briefly on close so Watchdog resumes Kiosk immediately
            Task.Run(ExitSetupMaintenanceAsync).Wait(1500);
        }
    }

    private async Task ExitSetupMaintenanceAsync()
    {
        string? token = Interlocked.Exchange(ref _activeAdminSessionToken, null);
        if (string.IsNullOrWhiteSpace(token)) return;
        try
        {
            await SendPipeAsync(IpcCommands.AdminExitMaintenanceMode, new AdminSessionRequest { SessionToken = token });
        }
        catch
        {
            // Ignore pipe errors during close
        }
    }

    private void SetBusy(bool busy)
    {
        _isBusy = busy;
        UpdateButtonStates();
    }

    private async Task<int> RunProcessStreamingAsync(ProcessStartInfo psi, Action<string>? onOutputLine = null)
    {
        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            onOutputLine?.Invoke(args.Data);
            Dispatcher.BeginInvoke(() => AppendLog(args.Data));
        };
        proc.ErrorDataReceived += (_, args) =>
        {
            if (args.Data is null) return;
            Dispatcher.BeginInvoke(() => AppendLog($"[ERR] {args.Data}"));
        };

        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
        await proc.WaitForExitAsync();
        return proc.ExitCode;
    }

    private async Task ApplyInitialScheduleViaPipeAsync(
        string adminPassword,
        int startH, int startM,
        int endH, int endM,
        int maxMinutes,
        int warnMinutes,
        string criteria)
    {
        for (int attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var authResp = await SendPipeAsync(IpcCommands.AuthenticateAdmin, new AdminAuthRequest { Credential = adminPassword });
                var auth = DeserializePayload<AdminAuthResult>(authResp);
                if (auth is null || !auth.Authenticated || string.IsNullOrWhiteSpace(auth.SessionToken)) return;

                _activeAdminSessionToken = auth.SessionToken;
                // Pause Kiosk UI briefly so the parent can clearly read and copy the Master Recovery Key
                await SendPipeAsync(IpcCommands.AdminEnterMaintenanceMode, new AdminSessionRequest { SessionToken = auth.SessionToken });

                var settingsResp = await SendPipeAsync(IpcCommands.AdminGetSettings, new AdminSessionRequest { SessionToken = auth.SessionToken });
                var current = DeserializePayload<AdminSettingsDto>(settingsResp);

                var weeklySchedule = WeeklyExpander.IsExpanded
                    ? BuildWeeklyRulesFromUi(startH, startM, endH, endM, maxMinutes)
                    : HomeworkRuleConfig.CreateDefaultWeeklySchedule(startH, startM, endH, endM, maxMinutes);

                var updateReq = new AdminUpdateSettingsRequest
                {
                    SessionToken = auth.SessionToken,
                    StartHour = startH,
                    StartMinute = startM,
                    EndHour = endH,
                    EndMinute = endM,
                    DailyMaxUsageMinutes = maxMinutes,
                    WarningMinutesBeforeLock = warnMinutes,
                    WeeklySchedule = weeklySchedule,
                    GeminiModel = current?.GeminiModel ?? AppBuildConfig.GeminiModelName,
                    DailyCriteria = string.IsNullOrWhiteSpace(criteria) ? (current?.DailyCriteria ?? string.Empty) : criteria,
                    WhitelistUrls = current?.WhitelistUrls is { Count: > 0 }
                        ? current.WhitelistUrls
                        : StudyWhitelistCatalog.GetDefaultUrls()
                };

                await SendPipeAsync(IpcCommands.AdminUpdateSettings, updateReq);
                AppendLog($"Zaman ve süre kuralları uygulandı: {startH:D2}:{startM:D2} - {endH:D2}:{endM:D2}, Limit: {maxMinutes} dk.");
                return;
            }
            catch
            {
                await Task.Delay(500);
            }
        }
    }

    private static async Task<IpcResponse> SendPipeAsync(string command, object? payload)
    {
        using var pipe = new NamedPipeClientStream(".", AppBuildConfig.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(3000);
        var req = new IpcRequest
        {
            Command = command,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload, IpcJson.Options)
        };
        var utf8NoBom = new UTF8Encoding(false);
        using var writer = new StreamWriter(pipe, utf8NoBom, leaveOpen: true) { AutoFlush = true };
        using var reader = new StreamReader(pipe, utf8NoBom, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(req, IpcJson.Options));
        string? line = await reader.ReadLineAsync();
        return string.IsNullOrWhiteSpace(line)
            ? IpcResponse.Fail(req.CorrelationId, "Boş yanıt")
            : JsonSerializer.Deserialize<IpcResponse>(line, IpcJson.Options) ?? IpcResponse.Fail(req.CorrelationId, "Ayrıştırma hatası");
    }

    private static T? DeserializePayload<T>(IpcResponse resp)
    {
        if (!resp.Success || string.IsNullOrWhiteSpace(resp.PayloadJson)) return default;
        return JsonSerializer.Deserialize<T>(resp.PayloadJson, IpcJson.Options);
    }

    private void AppendLog(string line)
    {
        LogBox.AppendText(line + Environment.NewLine);
        LogBox.ScrollToEnd();
    }

    private static bool TryParseTime(string text, out int hour, out int minute)
    {
        hour = 0;
        minute = 0;
        var parts = text.Trim().Split(':');
        return parts.Length == 2 &&
               int.TryParse(parts[0], out hour) && hour is >= 0 and <= 23 &&
               int.TryParse(parts[1], out minute) && minute is >= 0 and <= 59;
    }

    private List<DailyScheduleRule> BuildWeeklyRulesFromUi(int defSh, int defSm, int defEh, int defEm, int defMax)
    {
        var list = new List<DailyScheduleRule>();
        foreach (var row in _weeklyRows)
        {
            bool okS = TryParseTime(row.StartTimeText, out int sh, out int sm);
            bool okE = TryParseTime(row.EndTimeText, out int eh, out int em);
            bool okM = int.TryParse(row.MaxMinutesText.Trim(), out int maxMin) && maxMin >= 0;
            list.Add(new DailyScheduleRule
            {
                Day = row.Day,
                StartHour = okS ? sh : defSh,
                StartMinute = okS ? sm : defSm,
                EndHour = okE ? eh : defEh,
                EndMinute = okE ? em : defEm,
                MaxUsageMinutes = okM ? maxMin : defMax,
                IsDayBlocked = row.IsBlocked
            });
        }
        return list;
    }

    private static string GetComputerModel()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT Model FROM Win32_ComputerSystem");
            foreach (ManagementObject obj in searcher.Get())
            {
                return obj["Model"]?.ToString() ?? "Unknown";
            }
        }
        catch
        {
            // Ignore WMI query errors
        }
        return "Unknown";
    }
}
