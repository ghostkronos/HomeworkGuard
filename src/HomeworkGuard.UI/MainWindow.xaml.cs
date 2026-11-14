using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using HomeworkGuard.Core;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.Core.Models;
using HomeworkGuard.UI.Security;
using HomeworkGuard.UI.Services;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace HomeworkGuard.UI;

public partial class MainWindow : Window
{
    private readonly NamedPipeIpcClient _ipc = new();
    private readonly WebcamCaptureHelper _webcam = new();
    private readonly LowLevelKeyboardHook _keyboardHook = new();
    private readonly TrayIconManager _trayManager = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly bool _isAdminLauncherMode;

    private bool _authorizedToClose;
    private bool _adminModalOpen;
    private bool _webViewInitialized;
    private bool _isPulseRunning;
    private bool _isInTrayBackgroundMode;
    private bool _isSubmittingStage1;
    private bool _isSubmittingStage2;
    private string? _lastMaintenanceSessionToken;
    private List<string> _whitelistUrls = [];
    private byte[]? _selectedBytes;
    private string _selectedFileName = string.Empty;
    private string _selectedMimeType = string.Empty;

    public MainWindow()
    {
        _isAdminLauncherMode = Environment.GetCommandLineArgs()
            .Any(a => string.Equals(a, "--admin", StringComparison.OrdinalIgnoreCase));

        InitializeComponent();
        ConfigureWindowMode();

        _trayManager.ResumeProtectionClicked += () => Dispatcher.BeginInvoke(new Action(async () => await ResumeProtectionFromTrayAsync()));
        _trayManager.OpenAdminConsoleClicked += () => Dispatcher.BeginInvoke(new Action(async () => await OpenAdminAuthDialogAsync()));
        _trayManager.ForceLockClicked += () => Dispatcher.BeginInvoke(new Action(async () => await ForceLockFromTrayAsync()));

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _pollTimer.Tick += async (_, _) => await RefreshStatusAsync();

        Loaded += async (_, _) =>
        {
            if (_isAdminLauncherMode)
            {
                _authorizedToClose = true;
                RootContainer.Opacity = 1;
                await OpenAdminAuthDialogAsync();
                Close();
                return;
            }

            PlayEntranceAnimation();

            if (!AppBuildConfig.IS_DEV_SANDBOX_MODE)
            {
                _keyboardHook.Install();
                Activate();
            }

            await RefreshStatusAsync();
            _pollTimer.Start();
        };
    }

    private void PlayEntranceAnimation()
    {
        var sb = new Storyboard();
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(fade, RootContainer);
        Storyboard.SetTargetProperty(fade, new PropertyPath(OpacityProperty));
        sb.Children.Add(fade);

        var slide = new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(280)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        Storyboard.SetTarget(slide, RootContainer);
        Storyboard.SetTargetProperty(slide, new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.Y)"));
        sb.Children.Add(slide);
        sb.Begin();
    }

    private void SetPulseAnimation(bool active)
    {
        if (active == _isPulseRunning) return;
        _isPulseRunning = active;

        if (active)
        {
            var anim = new DoubleAnimation(1.0, 0.25, TimeSpan.FromMilliseconds(650))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            EvalPulseDot.BeginAnimation(OpacityProperty, anim);
        }
        else
        {
            EvalPulseDot.BeginAnimation(OpacityProperty, null);
            EvalPulseDot.Opacity = 1.0;
        }
    }

    private async void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MainTabs.SelectedIndex == 1 && !_webViewInitialized)
        {
            await InitializeWebViewSafeAsync();
            if (_webViewInitialized && _whitelistUrls.Count > 0 && string.IsNullOrEmpty(BrowserUrlBox.Text))
            {
                NavigateWhitelistUrl(_whitelistUrls[0]);
            }
        }
    }

    private void ConfigureWindowMode()
    {
        if (_isAdminLauncherMode)
        {
            // Minimal centered launcher shell just to host modal Admin dialogs cleanly when unlocked
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Width = 1;
            Height = 1;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            ShowInTaskbar = false;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            _authorizedToClose = true;
            return;
        }

        if (AppBuildConfig.IS_DEV_SANDBOX_MODE)
        {
            DevSandboxBanner.Visibility = Visibility.Visible;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;
            Topmost = false;
            Width = 960;
            Height = 680;
            WindowState = WindowState.Normal;
            _authorizedToClose = true;
        }
        else
        {
            DevSandboxBanner.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            Topmost = true;
            ShowInTaskbar = false;
            Left = SystemParameters.VirtualScreenLeft;
            Top = SystemParameters.VirtualScreenTop;
            Width = SystemParameters.VirtualScreenWidth;
            Height = SystemParameters.VirtualScreenHeight;

            _keyboardHook.AdminShortcutPressed += () => Dispatcher.BeginInvoke(new Action(async () => await OpenAdminAuthDialogAsync()));
            _keyboardHook.Install();
        }
    }

    private async Task InitializeWebViewSafeAsync()
    {
        try
        {
            string userDataFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "HomeworkGuard", "WebView2Cache");
            var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder);
            await EduWebView.EnsureCoreWebView2Async(env);

            EduWebView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            EduWebView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            EduWebView.CoreWebView2.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (IsUrlWhitelisted(args.Uri))
                {
                    EduWebView.CoreWebView2.Navigate(args.Uri);
                }
            };

            _webViewInitialized = true;
            WebViewFallbackText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _webViewInitialized = false;
            EduWebView.Visibility = Visibility.Collapsed;
            WebViewFallbackText.Text = $"Eğitim tarayıcısı başlatılamadı: {ex.Message}";
            WebViewFallbackText.Visibility = Visibility.Visible;
        }
    }

    private async Task RefreshStatusAsync()
    {
        var resp = await _ipc.SendAsync(IpcCommands.GetStatus, timeoutMs: 2500);
        if (!resp.Success)
        {
            SetStateBadge("#28151B", "#E11D48", "#F43F5E", "#FDA4AF", "Servis Bekleniyor");
            AlertBanner.Visibility = Visibility.Visible;
            AlertBannerText.Text = resp.Message;
            return;
        }

        var status = NamedPipeIpcClient.Unpack<ServiceRuntimeStatus>(resp);
        if (status is null) return;

        ApplyRuntimeStatus(status);
    }

    private void ApplyRuntimeStatus(ServiceRuntimeStatus status)
    {
        ClockText.Text = $"{status.EffectiveLocalNow:HH:mm}" + (status.IsTimeSyncedWithNtp ? "" : " (Yerel)");
        InternetBadgeText.Text = status.IsInternetAvailable ? "Çevrimiçi" : "Çevrimdışı";
        InternetDot.Fill = status.IsInternetAvailable
            ? new SolidColorBrush(Color.FromRgb(16, 185, 129))
            : new SolidColorBrush(Color.FromRgb(244, 63, 94));

        DailyCriteriaText.Text = status.DailyCriteria;
        string quotaLabel = status.DailyMaxUsageMinutes > 0
            ? $"Günlük Limit: {status.UsedMinutesToday}/{status.DailyMaxUsageMinutes} dk"
            : "Süre Limiti: Sınırsız";
        string dayPrefix = string.IsNullOrWhiteSpace(status.EffectiveDayName) ? "" : $" ({status.EffectiveDayName})";
        ScheduleText.Text = status.IsTodayBlocked
            ? $"Bugün{dayPrefix}: Bilgisayar Kullanımı Kapalı"
            : $"İzinli Saat{dayPrefix}: {status.StartHour:D2}:{status.StartMinute:D2} – {status.EndHour:D2}:{status.EndMinute:D2} • {quotaLabel}";

        UpdateWhitelistButtons(status.WhitelistUrls);

        switch (status.CurrentState)
        {
            case LockState.NightLock:
                if (!AppBuildConfig.IS_DEV_SANDBOX_MODE && _isInTrayBackgroundMode)
                {
                    RestoreFromTrayToKiosk();
                }
                SetStateBadge("#28151B", "#9F1239", "#F43F5E", "#FDA4AF", status.IsTodayBlocked ? "Gün Kapalı" : "Gece Kilidi");
                AlertBanner.Visibility = Visibility.Visible;
                AlertBannerText.Text = status.StatusMessage;
                SubmitStage1Btn.IsEnabled = false;
                SubmitStage2Btn.IsEnabled = false;
                break;

            case LockState.HomeworkRequired:
                if (!AppBuildConfig.IS_DEV_SANDBOX_MODE && _isInTrayBackgroundMode)
                {
                    RestoreFromTrayToKiosk();
                }
                SetStateBadge("#261E10", "#D97706", "#F59E0B", "#FCD34D", "Ödev Bekleniyor");
                SubmitStage1Btn.IsEnabled = !_isSubmittingStage1 && !_isSubmittingStage2 && status.IsInternetAvailable;
                SubmitStage2Btn.IsEnabled = !_isSubmittingStage1 && !_isSubmittingStage2 && status.IsInternetAvailable;
                if (!status.IsInternetAvailable)
                {
                    AlertBanner.Visibility = Visibility.Visible;
                    AlertBannerText.Text = "Ödev kontrolü için internet bağlantısı bekleniyor.";
                }
                else
                {
                    AlertBanner.Visibility = Visibility.Collapsed;
                }
                break;

            case LockState.UnlockedFreeTime:
                SetStateBadge("#0E241C", "#059669", "#10B981", "#6EE7B7", "Erişim Açık");
                AlertBanner.Visibility = status.ShowPreLockWarning ? Visibility.Visible : Visibility.Collapsed;
                AlertBannerText.Text = status.StatusMessage;

                if (!AppBuildConfig.IS_DEV_SANDBOX_MODE)
                {
                    EnterTrayBackgroundMode(LockState.UnlockedFreeTime, $"Serbest zaman ({status.RemainingFreeTimeText})");
                    return;
                }
                break;

            case LockState.MaintenanceMode:
                SetStateBadge("#0F212E", "#0284C7", "#38BDF8", "#7DD3FC", "Bakım Modu");
                if (!AppBuildConfig.IS_DEV_SANDBOX_MODE)
                {
                    EnterTrayBackgroundMode(LockState.MaintenanceMode, "Bakım Modu — Sağ tık ile korumayı başlatabilirsiniz");
                    return;
                }
                break;
        }

        if (!_isSubmittingStage1 && !_isSubmittingStage2)
        {
            UpdateEvaluationPanel(status);
        }
    }

    private void SetStateBadge(string bgHex, string borderHex, string dotHex, string textHex, string label)
    {
        StateBadge.Background = (SolidColorBrush)new BrushConverter().ConvertFromString(bgHex)!;
        StateBadge.BorderBrush = (SolidColorBrush)new BrushConverter().ConvertFromString(borderHex)!;
        StateDot.Fill = (SolidColorBrush)new BrushConverter().ConvertFromString(dotHex)!;
        StateBadgeText.Foreground = (SolidColorBrush)new BrushConverter().ConvertFromString(textHex)!;
        StateBadgeText.Text = label;
    }

    private void UpdateStepper(int activeStep, bool error = false)
    {
        ApplyStepStyle(Step1Pill, Step1Text, activeStep == 1, activeStep > 1, error && activeStep == 1);
        ApplyStepStyle(Step2Pill, Step2Text, activeStep == 2, activeStep > 2, error && activeStep == 2);
        ApplyStepStyle(Step3Pill, Step3Text, activeStep == 3, false, false);
    }

    private static void ApplyStepStyle(Border pill, TextBlock label, bool current, bool completed, bool error)
    {
        if (error)
        {
            pill.Background = new SolidColorBrush(Color.FromRgb(40, 21, 27));
            pill.BorderBrush = new SolidColorBrush(Color.FromRgb(225, 29, 72));
            label.Foreground = new SolidColorBrush(Color.FromRgb(253, 164, 175));
        }
        else if (completed)
        {
            pill.Background = new SolidColorBrush(Color.FromRgb(14, 36, 28));
            pill.BorderBrush = new SolidColorBrush(Color.FromRgb(16, 185, 129));
            label.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
        }
        else if (current)
        {
            pill.Background = new SolidColorBrush(Color.FromRgb(20, 43, 40));
            pill.BorderBrush = new SolidColorBrush(Color.FromRgb(13, 148, 136));
            label.Foreground = new SolidColorBrush(Color.FromRgb(94, 234, 212));
        }
        else
        {
            pill.Background = new SolidColorBrush(Color.FromRgb(22, 25, 29));
            pill.BorderBrush = new SolidColorBrush(Color.FromRgb(42, 48, 56));
            label.Foreground = new SolidColorBrush(Color.FromRgb(107, 114, 128));
        }
    }

    private void UpdateEvaluationPanel(ServiceRuntimeStatus status)
    {
        switch (status.EvaluationStage)
        {
            case HomeworkEvaluationStage.Idle:
                SetPulseAnimation(false);
                UpdateStepper(1);
                StageStatusText.Text = "Ödev bekleniyor";
                StageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(94, 234, 212));
                EvalPulseDot.Fill = new SolidColorBrush(Color.FromRgb(45, 212, 191));
                SocraticPanel.Visibility = Visibility.Collapsed;
                break;

            case HomeworkEvaluationStage.Stage1AnalyzingMedia:
                SetPulseAnimation(true);
                UpdateStepper(1);
                StageStatusText.Text = "Ödev inceleniyor...";
                StageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(252, 211, 77));
                EvalPulseDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
                AiFeedbackText.Text = status.LastAiFeedback;
                SocraticPanel.Visibility = Visibility.Collapsed;
                break;

            case HomeworkEvaluationStage.Stage2SocraticQuestions:
                SetPulseAnimation(false);
                UpdateStepper(2);
                StageStatusText.Text = "Kısa Değerlendirme Soruları";
                StageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
                EvalPulseDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                AiFeedbackText.Text = status.LastAiFeedback;
                if (status.SocraticQuestions.Count >= 2)
                {
                    Question1Label.Text = $"Soru 1 — {status.SocraticQuestions[0]}";
                    Question2Label.Text = $"Soru 2 — {status.SocraticQuestions[1]}";
                }
                if (SocraticPanel.Visibility != Visibility.Visible)
                {
                    SocraticPanel.Opacity = 0;
                    SocraticPanel.Visibility = Visibility.Visible;
                    SocraticPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)));
                }
                break;

            case HomeworkEvaluationStage.Rejected:
                SetPulseAnimation(false);
                UpdateStepper(1, error: true);
                StageStatusText.Text = status.LastScore.HasValue
                    ? $"Tekrar deneme gerekli (Puan: {status.LastScore}/100)"
                    : "Ödevde eksikler var";
                StageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(253, 164, 175));
                EvalPulseDot.Fill = new SolidColorBrush(Color.FromRgb(244, 63, 94));
                AiFeedbackText.Text = status.LastAiFeedback;
                break;

            case HomeworkEvaluationStage.Approved:
                SetPulseAnimation(false);
                UpdateStepper(3);
                StageStatusText.Text = $"Onaylandı (Puan: {status.LastScore ?? 100}/100)";
                StageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(110, 231, 183));
                EvalPulseDot.Fill = new SolidColorBrush(Color.FromRgb(16, 185, 129));
                AiFeedbackText.Text = status.LastAiFeedback;
                SocraticPanel.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private void UpdateWhitelistButtons(List<string> urls)
    {
        if (_whitelistUrls.SequenceEqual(urls)) return;
        _whitelistUrls = [.. urls];

        WhitelistButtonsPanel.Children.Clear();
        foreach (var url in _whitelistUrls)
        {
            var btn = new Button
            {
                Content = StudyWhitelistCatalog.GetFriendlyLabel(url),
                Style = (Style)FindResource("WhitelistPillButton"),
                Margin = new Thickness(0, 0, 8, 6),
                Tag = url,
                ToolTip = url
            };
            btn.Click += (_, _) => NavigateWhitelistUrl(url);
            WhitelistButtonsPanel.Children.Add(btn);
        }

        if (_webViewInitialized && _whitelistUrls.Count > 0 && string.IsNullOrEmpty(BrowserUrlBox.Text))
        {
            NavigateWhitelistUrl(_whitelistUrls[0]);
        }
    }

    private void NavigateWhitelistUrl(string url)
    {
        if (!_webViewInitialized || !IsUrlWhitelisted(url)) return;
        BrowserUrlBox.Text = url;
        EduWebView.CoreWebView2.Navigate(url);
    }

    private void EduWebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (!IsUrlWhitelisted(e.Uri))
        {
            e.Cancel = true;
            BrowserUrlBox.Text = $"İzin listesi dışında: {e.Uri}";
            ReportClientActivity("BROWSER_NAV_BLOCKED", $"Engellenen adres: {e.Uri}");
            return;
        }
        BrowserUrlBox.Text = e.Uri;
        ReportClientActivity("BROWSER_NAV_ALLOWED", $"Açılan eğitim sitesi: {e.Uri}");
    }

    private void ReportClientActivity(string eventType, string details)
    {
        _ = Task.Run(() => _ipc.SendAsync(IpcCommands.ReportClientEvent, new ClientActivityEventRequest
        {
            EventType = eventType,
            Details = details
        }, timeoutMs: 2500));
    }

    private bool IsUrlWhitelisted(string? targetUrl)
    {
        if (string.IsNullOrWhiteSpace(targetUrl)) return false;
        if (!Uri.TryCreate(targetUrl, UriKind.Absolute, out var targetUri)) return false;
        if (targetUri.Scheme != Uri.UriSchemeHttps && targetUri.Scheme != Uri.UriSchemeHttp) return false;

        foreach (var allowed in _whitelistUrls)
        {
            if (!Uri.TryCreate(allowed, UriKind.Absolute, out var allowedUri)) continue;
            if (targetUri.Host.Equals(allowedUri.Host, StringComparison.OrdinalIgnoreCase) ||
                targetUri.Host.EndsWith("." + allowedUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private void BrowserBack_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized && EduWebView.CanGoBack) EduWebView.GoBack();
    }

    private void BrowserRefresh_Click(object sender, RoutedEventArgs e)
    {
        if (_webViewInitialized) EduWebView.Reload();
    }

    private void DropZone_Click(object sender, MouseButtonEventArgs e) => UploadFile_Click(sender, e);

    private void DropZone_DragEnter(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            DropZoneBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(45, 212, 191));
            DropZoneBorder.Background = new SolidColorBrush(Color.FromRgb(18, 34, 34));
        }
        else
        {
            e.Effects = DragDropEffects.None;
        }
        e.Handled = true;
    }

    private void DropZone_DragLeave(object sender, DragEventArgs e)
    {
        DropZoneBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(46, 53, 64));
        DropZoneBorder.Background = new SolidColorBrush(Color.FromRgb(19, 22, 26));
    }

    private async void DropZone_Drop(object sender, DragEventArgs e)
    {
        DropZone_DragLeave(sender, e);
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            await LoadSelectedFileAsync(files[0]);
        }
    }

    private async void UploadFile_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Ödev Dosyası Seçin",
            Filter = "Desteklenen Dosyalar|*.jpg;*.jpeg;*.png;*.webp;*.pdf;*.txt;*.cs;*.py;*.js;*.cpp;*.java;*.md|Tüm Dosyalar|*.*"
        };

        if (dlg.ShowDialog() != true) return;
        await LoadSelectedFileAsync(dlg.FileName);
    }

    private async Task LoadSelectedFileAsync(string filePath)
    {
        if (!File.Exists(filePath)) return;

        byte[] bytes = await File.ReadAllBytesAsync(filePath);
        if (bytes.Length > 15 * 1024 * 1024)
        {
            MessageBox.Show("Dosya boyutu en fazla 15 MB olabilir.", "Uyarı", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _selectedBytes = bytes;
        _selectedFileName = Path.GetFileName(filePath);
        _selectedMimeType = DetectMimeType(_selectedFileName);
        SelectedFileInfoText.Text = $"{_selectedFileName} • {bytes.Length / 1024.0:F1} KB";
        SelectedFileInfoText.Foreground = new SolidColorBrush(Color.FromRgb(94, 234, 212));
        ReportClientActivity("FILE_SELECTED", $"Dosya: {_selectedFileName} ({bytes.Length / 1024.0:F1} KB)");

        if (_selectedMimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            var bmp = new BitmapImage();
            using var ms = new MemoryStream(bytes);
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
            bmp.Freeze();
            PreviewImage.Source = bmp;
            PreviewImage.Visibility = Visibility.Visible;
            PreviewPlaceholderPanel.Visibility = Visibility.Collapsed;
        }
        else
        {
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholderText.Text = $"{_selectedFileName}\nAnaliz için hazır.";
            PreviewPlaceholderPanel.Visibility = Visibility.Visible;
        }
    }

    private void CaptureWebcam_Click(object sender, RoutedEventArgs e)
    {
        var previewDialog = new CameraPreviewWindow { Owner = this };
        if (previewDialog.ShowDialog() != true ||
            previewDialog.CapturedJpegBytes is null ||
            previewDialog.CapturedPreview is null)
        {
            return;
        }

        _selectedBytes = previewDialog.CapturedJpegBytes;
        _selectedFileName = $"webcam_{DateTime.Now:yyyyMMdd_HHmmss}.jpg";
        _selectedMimeType = "image/jpeg";
        SelectedFileInfoText.Text = $"{_selectedFileName} • {_selectedBytes.Length / 1024.0:F1} KB";
        SelectedFileInfoText.Foreground = new SolidColorBrush(Color.FromRgb(94, 234, 212));
        ReportClientActivity("WEBCAM_CAPTURED", $"Kameradan fotoğraf çekildi: {_selectedFileName}");

        PreviewImage.Source = previewDialog.CapturedPreview;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholderPanel.Visibility = Visibility.Collapsed;
    }

    private async void SubmitStage1_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedBytes is null || _selectedBytes.Length == 0)
        {
            AiFeedbackText.Text = "Devam etmek için önce bir ödev dosyası seçin veya fotoğraf çekin.";
            return;
        }

        var req = new SubmitHomeworkMediaRequest
        {
            FileName = _selectedFileName,
            MimeType = _selectedMimeType,
            Base64Data = Convert.ToBase64String(_selectedBytes),
            StudentNote = StudentNoteBox.Text.Trim()
        };

        await SubmitHomeworkRequestAsync(
            isStage1: true,
            button: SubmitStage1Btn,
            idleLabel: "Ödevi Kontrol Et",
            waitingFeedback: "Çalışman değerlendiriliyor, birkaç saniye bekleyebilirsin...",
            command: IpcCommands.SubmitHomeworkMedia,
            payload: req);
    }

    private async void SubmitStage2_Click(object sender, RoutedEventArgs e)
    {
        var req = new SubmitSocraticAnswersRequest
        {
            Answer1 = Answer1Box.Text.Trim(),
            Answer2 = Answer2Box.Text.Trim()
        };

        await SubmitHomeworkRequestAsync(
            isStage1: false,
            button: SubmitStage2Btn,
            idleLabel: "Cevapları Gönder ve Kilidi Aç",
            waitingFeedback: "Cevapların değerlendiriliyor, birkaç saniye bekleyebilirsin...",
            command: IpcCommands.SubmitSocraticAnswers,
            payload: req);
    }

    /// <summary>
    /// Disables homework submission buttons with a loading label until the IPC response arrives.
    /// </summary>
    private async Task SubmitHomeworkRequestAsync(
        bool isStage1,
        Button button,
        string idleLabel,
        string waitingFeedback,
        string command,
        object payload)
    {
        if (_isSubmittingStage1 || _isSubmittingStage2) return;

        if (isStage1) _isSubmittingStage1 = true;
        else _isSubmittingStage2 = true;

        SubmitStage1Btn.IsEnabled = false;
        SubmitStage2Btn.IsEnabled = false;
        button.Content = "Ödev kontrol ediliyor...";
        SetPulseAnimation(true);
        StageStatusText.Text = "Ödev kontrol ediliyor...";
        StageStatusText.Foreground = new SolidColorBrush(Color.FromRgb(252, 211, 77));
        EvalPulseDot.Fill = new SolidColorBrush(Color.FromRgb(245, 158, 11));
        AiFeedbackText.Text = waitingFeedback;

        try
        {
            var resp = await _ipc.SendAsync(command, payload, timeoutMs: 90_000);
            _isSubmittingStage1 = false;
            _isSubmittingStage2 = false;
            button.Content = idleLabel;

            var status = NamedPipeIpcClient.Unpack<ServiceRuntimeStatus>(resp);
            if (status is not null)
            {
                ApplyRuntimeStatus(status);
            }
            else
            {
                SubmitStage1Btn.IsEnabled = true;
                SubmitStage2Btn.IsEnabled = true;
                SetPulseAnimation(false);
                AiFeedbackText.Text = resp.Message;
            }
        }
        finally
        {
            _isSubmittingStage1 = false;
            _isSubmittingStage2 = false;
            button.Content = idleLabel;
        }
    }

    private async void ResetSession_Click(object sender, RoutedEventArgs e)
    {
        Answer1Box.Clear();
        Answer2Box.Clear();
        var resp = await _ipc.SendAsync(IpcCommands.ResetHomeworkSession);
        var status = NamedPipeIpcClient.Unpack<ServiceRuntimeStatus>(resp);
        if (status is not null) ApplyRuntimeStatus(status);
    }

    private async void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool alt = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (ctrl && alt && shift && key == Key.F12)
        {
            e.Handled = true;
            await OpenAdminAuthDialogAsync();
        }
        else if (ctrl && key == Key.Tab && MainTabs.Items.Count > 1)
        {
            e.Handled = true;
            MainTabs.SelectedIndex = MainTabs.SelectedIndex == 0 ? 1 : 0;
        }
    }

    private async Task OpenAdminAuthDialogAsync()
    {
        if (_adminModalOpen) return;
        _adminModalOpen = true;
        try
        {
            var ownerWindow = IsVisible && !_isInTrayBackgroundMode ? this : null;
            var authWin = new AdminAuthWindow(_ipc)
            {
                Owner = ownerWindow,
                WindowStartupLocation = ownerWindow is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
            };
            if (authWin.ShowDialog() == true && authWin.AuthResult is not null)
            {
                var console = new AdminConsoleWindow(_ipc, authWin.AuthResult)
                {
                    Owner = ownerWindow,
                    WindowStartupLocation = ownerWindow is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner
                };
                console.ShowDialog();

                if (console.SafeExitRequested)
                {
                    _lastMaintenanceSessionToken = console.AdminSessionToken;
                    if (!AppBuildConfig.IS_DEV_SANDBOX_MODE && !_isAdminLauncherMode)
                    {
                        EnterTrayBackgroundMode(LockState.MaintenanceMode, "Bakım Modu — Sağ tık ile korumayı başlatabilirsiniz");
                        return;
                    }
                    PerformAuthorizedClose();
                    return;
                }

                if (console.StatusChanged && !_isAdminLauncherMode)
                {
                    await RefreshStatusAsync();
                }
            }
        }
        finally
        {
            _adminModalOpen = false;
        }
    }

    private void EnterTrayBackgroundMode(LockState state, string summary)
    {
        if (_isInTrayBackgroundMode)
        {
            _trayManager.UpdateState(state, summary);
            return;
        }

        _isInTrayBackgroundMode = true;
        _keyboardHook.Uninstall();
        Topmost = false;
        ShowInTaskbar = false;
        Hide();
        _trayManager.Show(state, summary);
    }

    private void RestoreFromTrayToKiosk()
    {
        _isInTrayBackgroundMode = false;
        _lastMaintenanceSessionToken = null;
        _trayManager.Hide();

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        Left = SystemParameters.VirtualScreenLeft;
        Top = SystemParameters.VirtualScreenTop;
        Width = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;
        ShowInTaskbar = false;
        Topmost = true;
        WindowState = WindowState.Normal;
        Show();
        Activate();
        _keyboardHook.Install();
    }

    private async Task ResumeProtectionFromTrayAsync()
    {
        if (_adminModalOpen) return;

        if (!string.IsNullOrWhiteSpace(_lastMaintenanceSessionToken))
        {
            var resp = await _ipc.SendAsync(IpcCommands.AdminExitMaintenanceMode, new AdminSessionRequest { SessionToken = _lastMaintenanceSessionToken });
            if (resp.Success)
            {
                _lastMaintenanceSessionToken = null;
                await RefreshStatusAsync();
                return;
            }
        }

        _adminModalOpen = true;
        try
        {
            var authWin = new AdminAuthWindow(_ipc) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (authWin.ShowDialog() == true && authWin.AuthResult is not null)
            {
                var resp = await _ipc.SendAsync(IpcCommands.AdminExitMaintenanceMode, new AdminSessionRequest { SessionToken = authWin.AuthResult.SessionToken });
                if (resp.Success)
                {
                    _lastMaintenanceSessionToken = null;
                    await RefreshStatusAsync();
                }
            }
        }
        finally
        {
            _adminModalOpen = false;
        }
    }

    private async Task ForceLockFromTrayAsync()
    {
        if (_adminModalOpen) return;
        _adminModalOpen = true;
        try
        {
            var authWin = new AdminAuthWindow(_ipc) { WindowStartupLocation = WindowStartupLocation.CenterScreen };
            if (authWin.ShowDialog() == true && authWin.AuthResult is not null)
            {
                await _ipc.SendAsync(IpcCommands.AdminExitMaintenanceMode, new AdminSessionRequest { SessionToken = authWin.AuthResult.SessionToken });
                await _ipc.SendAsync(IpcCommands.AdminForceLock, new AdminSessionRequest { SessionToken = authWin.AuthResult.SessionToken });
                await RefreshStatusAsync();
            }
        }
        finally
        {
            _adminModalOpen = false;
        }
    }

    private void PerformAuthorizedClose()
    {
        _authorizedToClose = true;
        _pollTimer.Stop();
        _keyboardHook.Uninstall();
        _trayManager.Dispose();
        Close();
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!AppBuildConfig.IS_DEV_SANDBOX_MODE && !_authorizedToClose && !_isAdminLauncherMode)
        {
            e.Cancel = true;
            return;
        }

        _pollTimer.Stop();
        _keyboardHook.Dispose();
        _trayManager.Dispose();
        await _webcam.DisposeAsync();
    }

    private static string DetectMimeType(string fileName)
    {
        return Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "image/jpeg",
            ".png" => "image/png",
            ".webp" => "image/webp",
            ".pdf" => "application/pdf",
            _ => "text/plain"
        };
    }
}
