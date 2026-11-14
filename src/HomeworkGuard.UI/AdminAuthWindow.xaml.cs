using System.Windows;
using System.Windows.Input;
using HomeworkGuard.Core.Ipc;
using HomeworkGuard.UI.Services;

namespace HomeworkGuard.UI;

public partial class AdminAuthWindow : Window
{
    private readonly NamedPipeIpcClient _ipc;

    public AdminAuthResult? AuthResult { get; private set; }

    public AdminAuthWindow(NamedPipeIpcClient ipc)
    {
        _ipc = ipc;
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            Activate();
            CredentialBox.Focus();
            Keyboard.Focus(CredentialBox);
            await Task.Delay(50);
            Activate();
            CredentialBox.Focus();
            Keyboard.Focus(CredentialBox);
        };
    }

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        await AuthenticateAsync();
    }

    private async void CredentialBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            await AuthenticateAsync();
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private async Task AuthenticateAsync()
    {
        string secret = CredentialBox.Password;
        if (string.IsNullOrWhiteSpace(secret))
        {
            ErrorText.Text = "Lütfen şifre, OTP veya kurtarma anahtarı girin.";
            return;
        }

        VerifyBtn.IsEnabled = false;
        ErrorText.Text = "Doğrulanıyor...";

        var resp = await _ipc.SendAsync(IpcCommands.AuthenticateAdmin, new AdminAuthRequest { Credential = secret });
        VerifyBtn.IsEnabled = true;

        if (!resp.Success)
        {
            ErrorText.Text = resp.Message;
            return;
        }

        var auth = NamedPipeIpcClient.Unpack<AdminAuthResult>(resp);
        if (auth is null)
        {
            ErrorText.Text = "Sunucudan geçersiz doğrulama yanıtı alındı.";
            return;
        }

        if (auth.Authenticated)
        {
            AuthResult = auth;
            DialogResult = true;
            Close();
            return;
        }

        CredentialBox.Clear();
        if (auth.IsLockedOut && auth.LockoutUntilUtc.HasValue)
        {
            var localUntil = auth.LockoutUntilUtc.Value.ToLocalTime();
            ErrorText.Text = $"3 hatalı deneme! Giriş {localUntil:HH:mm:ss} tarihine kadar (15 dk) kilitlendi ve Telegram uyarısı gönderildi.";
        }
        else
        {
            ErrorText.Text = $"Hatalı kimlik bilgisi. Kalan deneme hakkı: {auth.RemainingAttempts}";
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
