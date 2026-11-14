using System.Runtime.InteropServices;
using System.Security.Principal;
using HomeworkGuard.Core;
using Microsoft.Win32;

namespace HomeworkGuard.Service.Security;

/// <summary>
/// Protects installed HomeworkGuard binaries and OS kiosk policies against child tampering
/// even while the PC is in UnlockedFreeTime. Releases all locks and OS restrictions when an
/// authenticated administrator enters MaintenanceMode.
/// Strictly a no-op when IS_DEV_SANDBOX_MODE is true (Host PC protection).
/// </summary>
public sealed class TamperProtectionService : IDisposable
{
    private readonly ILogger<TamperProtectionService> _logger;
    private readonly object _lock = new();
    private readonly List<FileStream> _heldFileLocks = [];
    private bool _isSuspendedForMaintenance;

    private static readonly string[] ProtectedBinaryNames =
    [
        "HomeworkGuard.Service.exe",
        "HomeworkGuard.Service.dll",
        "HomeworkGuard.UI.exe",
        "HomeworkGuard.UI.dll",
        "HomeworkGuard.Core.dll",
        "Install-Hardening.ps1",
        "Uninstall-Hardening.ps1"
    ];

    private const string SystemPoliciesSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System";

    public TamperProtectionService(ILogger<TamperProtectionService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Enforces binary read-locks and OS hardening policies while protection is active.
    /// Safe no-op in Dev Sandbox mode.
    /// </summary>
    public void EnforceProtection()
    {
        if (AppBuildConfig.IS_DEV_SANDBOX_MODE) return;

        lock (_lock)
        {
            _isSuspendedForMaintenance = false;
            EnsureFileLocksLocked();
            ApplyUserAndSystemPolicies(enableRestrictions: true);
        }
    }

    /// <summary>
    /// Periodically called by Watchdog while not in MaintenanceMode so OS restrictions
    /// cannot be silently flipped back while UnlockedFreeTime desktop access is active.
    /// </summary>
    public void PeriodicVerify()
    {
        if (AppBuildConfig.IS_DEV_SANDBOX_MODE) return;

        lock (_lock)
        {
            if (_isSuspendedForMaintenance) return;
            EnsureFileLocksLocked();
            ApplyUserAndSystemPolicies(enableRestrictions: true);
        }
    }

    /// <summary>
    /// Releases binary locks and temporarily lifts OS restrictions so an authenticated Admin
    /// has full control over the machine during MaintenanceMode.
    /// </summary>
    public void SuspendForMaintenance()
    {
        if (AppBuildConfig.IS_DEV_SANDBOX_MODE) return;

        lock (_lock)
        {
            _isSuspendedForMaintenance = true;
            ReleaseFileLocksLocked();
            ApplyUserAndSystemPolicies(enableRestrictions: false);
            _logger.LogInformation("TamperProtection: Bakım modu için dosya kilitleri ve OS kısıtlamaları askıya alındı.");
        }
    }

    private void EnsureFileLocksLocked()
    {
        if (_heldFileLocks.Count > 0) return;

        string baseDir = AppContext.BaseDirectory;
        foreach (string fileName in ProtectedBinaryNames)
        {
            string fullPath = Path.Combine(baseDir, fileName);
            if (!File.Exists(fullPath)) continue;

            try
            {
                // FileShare.Read permits reading & CreateProcess execution while blocking rename/write/delete.
                var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
                _heldFileLocks.Add(fs);
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dosya koruma kilidi alınamadı: {File}", fullPath);
            }
        }
    }

    private void ReleaseFileLocksLocked()
    {
        foreach (var fs in _heldFileLocks)
        {
            try { fs.Dispose(); } catch { }
        }
        _heldFileLocks.Clear();
    }

    private void ApplyUserAndSystemPolicies(bool enableRestrictions)
    {
        try
        {
            // HKLM system-wide policies
            using (var hklmSys = Registry.LocalMachine.CreateSubKey(SystemPoliciesSubKey, writable: true))
            {
                SetOrClearDword(hklmSys, "HideFastUserSwitching", enableRestrictions ? 1 : null);
                SetOrClearDword(hklmSys, "DisableLockWorkstation", enableRestrictions ? 1 : null);
            }

            // Loaded interactive user profile hives (SIDs under HKEY_USERS)
            foreach (string sid in EnumerateInteractiveUserSids())
            {
                try
                {
                    string userPolPath = $@"{sid}\{SystemPoliciesSubKey}";
                    using var userSys = Registry.Users.CreateSubKey(userPolPath, writable: true);
                    SetOrClearDword(userSys, "DisableTaskMgr", enableRestrictions ? 1 : null);
                    SetOrClearDword(userSys, "DisableChangePassword", enableRestrictions ? 1 : null);
                    SetOrClearDword(userSys, "DisableRegistryTools", enableRestrictions ? 1 : null);
                    SetOrClearDword(userSys, "DisableLockWorkstation", enableRestrictions ? 1 : null);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Kullanıcı ilkesi güncellenemedi (SID: {Sid})", sid);
                }
            }

            BroadcastPolicyChange();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "OS politika güncellemesi sırasında hata.");
        }
    }

    private static IEnumerable<string> EnumerateInteractiveUserSids()
    {
        foreach (string sub in Registry.Users.GetSubKeyNames())
        {
            if (sub.StartsWith("S-1-5-21-", StringComparison.OrdinalIgnoreCase) &&
                !sub.EndsWith("_Classes", StringComparison.OrdinalIgnoreCase))
            {
                yield return sub;
            }
        }
    }

    private static void SetOrClearDword(RegistryKey? key, string name, int? value)
    {
        if (key is null) return;
        if (value.HasValue)
        {
            object? current = key.GetValue(name);
            if (current is int curInt && curInt == value.Value) return;
            key.SetValue(name, value.Value, RegistryValueKind.DWord);
        }
        else
        {
            if (key.GetValue(name) is not null)
            {
                key.DeleteValue(name, throwOnMissingValue: false);
            }
        }
    }

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageTimeout(
        IntPtr hWnd,
        uint Msg,
        UIntPtr wParam,
        string lParam,
        uint fuFlags,
        uint uTimeout,
        out UIntPtr lpdwResult);

    private static void BroadcastPolicyChange()
    {
        try
        {
            const int HWND_BROADCAST = 0xffff;
            const uint WM_SETTINGCHANGE = 0x001A;
            const uint SMTO_ABORTIFHUNG = 0x0002;
            SendMessageTimeout(new IntPtr(HWND_BROADCAST), WM_SETTINGCHANGE, UIntPtr.Zero, "Policy", SMTO_ABORTIFHUNG, 200, out _);
        }
        catch { }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            ReleaseFileLocksLocked();
        }
    }
}
