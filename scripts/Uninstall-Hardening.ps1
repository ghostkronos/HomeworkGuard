<#
.SYNOPSIS
    Cleanly removes HomeworkGuard.Service, releases runtime binary locks, and restores every
    modified registry value & system setting back to the pre-install snapshot in original_system_state.json.
#>
$ErrorActionPreference = "Stop"

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Bu betik Yönetici (Administrator) yetkisiyle çalıştırılmalıdır."
}

$ServiceName = "HomeworkGuardService"
$InstallDir = "C:\Program Files\HomeworkGuard"
$DataDir = "C:\ProgramData\HomeworkGuard"
$BackupStatePath = Join-Path $DataDir "original_system_state.json"
$AdminShortcutPath = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\HomeworkGuard Yönetici.lnk"

Write-Host "=== HomeworkGuard Kaldırma & Tam Sistem Geri Yükleme ===" -ForegroundColor Cyan

# 1. Stop Service & UI (this also releases TamperProtection file locks cleanly)
Write-Host "[1/5] Servis durduruluyor ve kaldırılıyor..." -ForegroundColor Yellow
Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
Get-Process -Name "HomeworkGuard.UI" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Seconds 2
& sc.exe delete $ServiceName 2>$null | Out-Null

foreach ($mode in @("Minimal", "Network")) {
    Remove-Item -Path "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName" -Recurse -Force -ErrorAction SilentlyContinue
}

# 2. Restore original Registry values from snapshot (or fallback cleanup if snapshot absent)
Write-Host "[2/5] Kayıt defteri (Registry) değerleri kurulum öncesi haline döndürülüyor..." -ForegroundColor Yellow
$snapshot = $null
if (Test-Path $BackupStatePath) {
    try {
        $snapshot = Get-Content -Path $BackupStatePath -Raw -Encoding UTF8 | ConvertFrom-Json
    } catch {
        Write-Host "Uyarı: Yedek durum okunamadı, varsayılan temizlik uygulanacak." -ForegroundColor DarkYellow
    }
}

if ($null -ne $snapshot -and $null -ne $snapshot.RegistryValues) {
    foreach ($entry in $snapshot.RegistryValues) {
        $regPath = $entry.Path
        $valName = $entry.Name
        try {
            if ($entry.ValueExistedBefore -eq $true) {
                if (-not (Test-Path $regPath)) {
                    New-Item -Path $regPath -Force | Out-Null
                }
                $kind = [Microsoft.Win32.RegistryValueKind]::DWord
                if (-not [string]::IsNullOrWhiteSpace($entry.ValueKind)) {
                    $kind = [Enum]::Parse([Microsoft.Win32.RegistryValueKind], $entry.ValueKind, $true)
                }
                Set-ItemProperty -Path $regPath -Name $valName -Value $entry.OriginalValue -Type $kind
                Write-Host "  Geri yüklendi: $regPath\$valName = $($entry.OriginalValue)" -ForegroundColor Gray
            }
            else {
                if (Test-Path $regPath) {
                    Remove-ItemProperty -Path $regPath -Name $valName -ErrorAction SilentlyContinue
                    Write-Host "  Kaldırıldı (kurulum öncesi yoktu): $regPath\$valName" -ForegroundColor Gray
                }
            }
        }
        catch {
            Write-Host "  Atlandı ($regPath\$valName): $($_.Exception.Message)" -ForegroundColor DarkGray
        }
    }

    # Remove newly-created empty subkeys whose parent did not exist before install
    foreach ($entry in ($snapshot.RegistryValues | Where-Object { $_.KeyExistedBefore -eq $false } | Select-Object -Property Path -Unique)) {
        try {
            if (Test-Path $entry.Path) {
                $k = Get-Item -Path $entry.Path -ErrorAction SilentlyContinue
                if ($null -ne $k -and $k.Property.Count -eq 0 -and $k.SubKeyCount -eq 0) {
                    Remove-Item -Path $entry.Path -Force -ErrorAction SilentlyContinue
                }
            }
        } catch {}
    }
}
else {
    $hklmSysPol = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    Remove-ItemProperty -Path $hklmSysPol -Name "HideFastUserSwitching" -ErrorAction SilentlyContinue
    Remove-ItemProperty -Path $hklmSysPol -Name "DisableLockWorkstation" -ErrorAction SilentlyContinue

    $hkcuSysPol = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    foreach ($v in @("DisableTaskMgr", "DisableChangePassword", "DisableRegistryTools", "DisableLockWorkstation")) {
        Remove-ItemProperty -Path $hkcuSysPol -Name $v -ErrorAction SilentlyContinue
    }
}

# 3. Remove Admin shortcut unless it existed before
Write-Host "[3/5] Yönetici kısayolu temizleniyor..." -ForegroundColor Yellow
if (($null -eq $snapshot) -or ($snapshot.AdminShortcutExistedBefore -ne $true)) {
    Remove-Item -Path $AdminShortcutPath -Force -ErrorAction SilentlyContinue
}

# 4. Broadcast WM_SETTINGCHANGE so Task Manager / Explorer reflect restored policies immediately
Write-Host "[4/5] Sistem politikaları yenileniyor..." -ForegroundColor Yellow
try {
    Add-Type -Namespace "HGWin32" -Name "NativeBroadcast" -MemberDefinition @"
[System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true, CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
public static extern System.IntPtr SendMessageTimeout(System.IntPtr hWnd, uint Msg, System.UIntPtr wParam, string lParam, uint fuFlags, uint uTimeout, out System.UIntPtr lpdwResult);
"@ -ErrorAction SilentlyContinue
    [UIntPtr]$res = [UIntPtr]::Zero
    [void][HGWin32.NativeBroadcast]::SendMessageTimeout([IntPtr]0xffff, 0x001A, [UIntPtr]::Zero, "Policy", 2, 200, [ref]$res)
} catch {}

# 5. Clean up C:\Program Files\HomeworkGuard and C:\ProgramData\HomeworkGuard
Write-Host "[5/5] Uygulama dosyaları ve korunan veri klasörü temizleniyor..." -ForegroundColor Yellow
Set-Location $env:TEMP
if (($null -eq $snapshot) -or ($snapshot.InstallDirExistedBefore -ne $true)) {
    if (Test-Path $InstallDir) {
        Remove-Item -Path $InstallDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
if (Test-Path $DataDir) {
    Remove-Item -Path $DataDir -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "HomeworkGuard tamamen kaldırıldı ve tüm sistem ayarları kurulum öncesi durumuna döndürüldü." -ForegroundColor Green
