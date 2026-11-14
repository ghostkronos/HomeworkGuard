<#
.SYNOPSIS
    Installs HomeworkGuard.Service (LocalSystem), snapshots original OS state before modifying anything,
    secures NTFS ACLs (Program Files + ProgramData) & Service DACL, registers SafeBoot entries,
    and applies OS kiosk hardening policies.
.NOTES
    WARNING: RUN ONLY INSIDE THE TARGET/VM MACHINE! NEVER RUN ON DEVELOPER HOST PC!
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$AdminPassword,

    [string]$GeminiApiKey = "",
    [string]$TelegramBotToken = "",
    [string]$AdminChatId = "",
    [string]$DailyCriteria = "",

    [switch]$ConfirmVmHardening,
    [switch]$SkipOsPolicyHardening
)

$ErrorActionPreference = "Stop"

# Safety Guard against accidental execution on developer Host PC
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    throw "Bu betik Yönetici (Administrator) yetkisiyle çalıştırılmalıdır."
}

$computerModel = (Get-CimInstance Win32_ComputerSystem).Model
$isVm = ($computerModel -match "VirtualBox|VMware|KVM|QEMU|Hyper-V|Bochs")
if (-not $isVm -and -not $ConfirmVmHardening) {
    throw "GÜVENLİK ENGELİ: Bu makine sanal makine gibi görünmüyor (Model: '$computerModel'). Host PC'yi korumak için kurulum durduruldu!"
}

if (-not $ConfirmVmHardening) {
    $ans = Read-Host "BU İŞLEM KIOSK KİLİDİNİ VE OS SIKILAŞTIRMASINI AKTİF EDER. Devam etmek için 'EVET' yazın"
    if ($ans -ne "EVET") {
        throw "Kullanıcı tarafından iptal edildi."
    }
}

$SourceDir = $PSScriptRoot
$InstallDir = "C:\Program Files\HomeworkGuard"
$DataDir = "C:\ProgramData\HomeworkGuard"
$BackupStatePath = Join-Path $DataDir "original_system_state.json"
$ServiceName = "HomeworkGuardService"
$ServiceExe = Join-Path $InstallDir "HomeworkGuard.Service.exe"
$UiExe = Join-Path $InstallDir "HomeworkGuard.UI.exe"
$AdminShortcutPath = "C:\ProgramData\Microsoft\Windows\Start Menu\Programs\HomeworkGuard Yönetici.lnk"

function Get-TargetUserSids {
    $sids = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    try {
        $currentSid = [System.Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        if ($currentSid -match '^S-1-5-21-') { [void]$sids.Add($currentSid) }
    } catch {}
    try {
        if (Test-Path "Registry::HKEY_USERS") {
            Get-ChildItem "Registry::HKEY_USERS" -ErrorAction SilentlyContinue | ForEach-Object {
                $name = $_.PSChildName
                if ($name -match '^S-1-5-21-' -and $name -notmatch '_Classes$') {
                    [void]$sids.Add($name)
                }
            }
        }
    } catch {}
    return @($sids)
}

function Capture-RegistryEntrySnapshot {
    param([string]$RegPath, [string]$ValueName)
    $keyExists = Test-Path $RegPath
    $valExists = $false
    $valKind = ""
    $valData = $null
    if ($keyExists) {
        try {
            $item = Get-Item -Path $RegPath -ErrorAction Stop
            if ($item.Property -contains $ValueName) {
                $valExists = $true
                $valKind = $item.GetValueKind($ValueName).ToString()
                $valData = $item.GetValue($ValueName, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        } catch {}
    }
    return [ordered]@{
        Path               = $RegPath
        Name               = $ValueName
        KeyExistedBefore   = $keyExists
        ValueExistedBefore = $valExists
        ValueKind          = $valKind
        OriginalValue      = $valData
    }
}

Write-Host "=== HomeworkGuard Kurulum & Hardening Başlatılıyor ===" -ForegroundColor Cyan

# 1. Stop existing service/UI if present
$existingSvc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existingSvc) {
    Write-Host "[1/7] Mevcut servis durduruluyor..." -ForegroundColor Yellow
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
    Start-Sleep -Seconds 2
    & sc.exe delete $ServiceName | Out-Null
    Start-Sleep -Seconds 1
}
Get-Process -Name "HomeworkGuard.UI" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue

# 2. Secure C:\ProgramData\HomeworkGuard first & snapshot original OS state ONCE (before any changes!)
Write-Host "[2/7] $DataDir hazırlanıyor ve mevcut sistem durumu yedekleniyor..." -ForegroundColor Yellow
$installDirExisted = Test-Path $InstallDir
$adminShortcutExisted = Test-Path $AdminShortcutPath
$serviceExisted = ($null -ne $existingSvc)

New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
$dataAcl = Get-Acl $DataDir
$dataAcl.SetAccessRuleProtection($true, $false)
foreach ($rule in @($dataAcl.Access)) { $dataAcl.RemoveAccessRule($rule) | Out-Null }
$dataAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$dataAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl -Path $DataDir -AclObject $dataAcl

$targetSids = Get-TargetUserSids
if (-not (Test-Path $BackupStatePath)) {
    $regSnapshots = @()
    $hklmSysPol = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    $regSnapshots += Capture-RegistryEntrySnapshot -RegPath $hklmSysPol -ValueName "HideFastUserSwitching"
    $regSnapshots += Capture-RegistryEntrySnapshot -RegPath $hklmSysPol -ValueName "DisableLockWorkstation"

    $hkcuSysPol = "HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    foreach ($valName in @("DisableTaskMgr", "DisableChangePassword", "DisableRegistryTools", "DisableLockWorkstation")) {
        $regSnapshots += Capture-RegistryEntrySnapshot -RegPath $hkcuSysPol -ValueName $valName
    }

    foreach ($sid in $targetSids) {
        $sidSysPol = "Registry::HKEY_USERS\$sid\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
        foreach ($valName in @("DisableTaskMgr", "DisableChangePassword", "DisableRegistryTools", "DisableLockWorkstation")) {
            $regSnapshots += Capture-RegistryEntrySnapshot -RegPath $sidSysPol -ValueName $valName
        }
    }

    $snapshot = [ordered]@{
        CapturedAtUtc             = (Get-Date).ToUniversalTime().ToString("o")
        InstallDirExistedBefore   = $installDirExisted
        ServiceExistedBefore      = $serviceExisted
        AdminShortcutExistedBefore = $adminShortcutExisted
        RegistryValues            = $regSnapshots
    }
    $snapshot | ConvertTo-Json -Depth 6 | Set-Content -Path $BackupStatePath -Encoding UTF8
    Write-Host "Sistem durum yedeği kaydedildi: $BackupStatePath" -ForegroundColor Gray
}

# 3. Copy binaries to C:\Program Files\HomeworkGuard and lock down NTFS ACL (Standard users cannot edit/delete)
Write-Host "[3/7] Uygulama dosyaları $InstallDir dizinine kopyalanıyor ve NTFS koruması uygulanıyor..." -ForegroundColor Yellow
New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
Copy-Item -Path (Join-Path $SourceDir "*") -Destination $InstallDir -Recurse -Force

$binAcl = Get-Acl $InstallDir
$binAcl.SetAccessRuleProtection($true, $false)
foreach ($rule in @($binAcl.Access)) { $binAcl.RemoveAccessRule($rule) | Out-Null }
$binAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("NT AUTHORITY\SYSTEM", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$binAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Administrators", "FullControl", "ContainerInherit,ObjectInherit", "None", "Allow")))
$binAcl.AddAccessRule((New-Object System.Security.AccessControl.FileSystemAccessRule("BUILTIN\Users", "ReadAndExecute", "ContainerInherit,ObjectInherit", "None", "Allow")))
Set-Acl -Path $InstallDir -AclObject $binAcl

# 4. Initialize database & secrets via HomeworkGuard.Service.exe --init-config
Write-Host "[4/7] Veritabanı, PBKDF2 şifre özeti ve Master Recovery Key oluşturuluyor..." -ForegroundColor Yellow
$initArgs = @("--init-config", "--admin-password", $AdminPassword)
if (-not [string]::IsNullOrWhiteSpace($GeminiApiKey)) { $initArgs += @("--gemini-key", $GeminiApiKey) }
if (-not [string]::IsNullOrWhiteSpace($TelegramBotToken)) { $initArgs += @("--telegram-token", $TelegramBotToken) }
if (-not [string]::IsNullOrWhiteSpace($AdminChatId)) { $initArgs += @("--chat-id", $AdminChatId) }
if (-not [string]::IsNullOrWhiteSpace($DailyCriteria)) { $initArgs += @("--criteria", $DailyCriteria) }

& $ServiceExe @initArgs
if ($LASTEXITCODE -ne 0) { throw "İlk yapılandırma (--init-config) başarısız oldu." }

# 5. Register Windows Service (LocalSystem + Auto-Restart Recovery + SafeBoot + Hardened DACL)
Write-Host "[5/7] Windows Servisi ($ServiceName) LocalSystem ve koruma DACL'i ile kaydediliyor..." -ForegroundColor Yellow
& sc.exe create $ServiceName binPath= "`"$ServiceExe`"" start= auto obj= "LocalSystem" DisplayName= "HomeworkGuard Watchdog Service" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "sc create başarısız oldu." }

& sc.exe description $ServiceName "HomeworkGuard AI Kiosk ve Ödev Denetim Bekçisi" | Out-Null
& sc.exe failure $ServiceName reset= 0 actions= restart/1000/restart/1000/restart/1000 | Out-Null
# Interactive/standard users can only query status; only SYSTEM & Builtin Administrators can stop/delete.
& sc.exe sdset $ServiceName "D:(A;;CCLCSWRPWPDTLOCRRC;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;CCLCSWLOCRRC;;;IU)(A;;CCLCSWLOCRRC;;;SU)" | Out-Null

# SafeBoot registration (keeps service alive even in Windows Safe Mode)
foreach ($mode in @("Minimal", "Network")) {
    $sbPath = "HKLM:\SYSTEM\CurrentControlSet\Control\SafeBoot\$mode\$ServiceName"
    if (-not (Test-Path $sbPath)) { New-Item -Path $sbPath -Force | Out-Null }
    Set-ItemProperty -Path $sbPath -Name "(Default)" -Value "Service" -Type String
}

# 6. Create Start Menu shortcut for Admin access even when PC is unlocked
Write-Host "[6/7] Yönetici Başlat Menüsü kısayolu oluşturuluyor..." -ForegroundColor Yellow
try {
    $shell = New-Object -ComObject WScript.Shell
    $sc = $shell.CreateShortcut($AdminShortcutPath)
    $sc.TargetPath = $UiExe
    $sc.Arguments = "--admin"
    $sc.WorkingDirectory = $InstallDir
    $sc.Description = "HomeworkGuard Yönetici Konsolu"
    $sc.Save()
} catch {
    Write-Host "Kısayol oluşturma atlandı: $($_.Exception.Message)" -ForegroundColor DarkGray
}

# 7. Apply OS Hardening Registry Restrictions
if (-not $SkipOsPolicyHardening) {
    Write-Host "[7/7] İşletim sistemi Kiosk sıkılaştırma politikaları uygulanıyor..." -ForegroundColor Yellow
    $hklmSysPol = "HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    if (-not (Test-Path $hklmSysPol)) { New-Item -Path $hklmSysPol -Force | Out-Null }
    Set-ItemProperty -Path $hklmSysPol -Name "HideFastUserSwitching" -Value 1 -Type DWord
    Set-ItemProperty -Path $hklmSysPol -Name "DisableLockWorkstation" -Value 1 -Type DWord

    $userPolicyPaths = @("HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System")
    foreach ($sid in $targetSids) {
        $userPolicyPaths += "Registry::HKEY_USERS\$sid\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System"
    }
    foreach ($polPath in ($userPolicyPaths | Select-Object -Unique)) {
        if (-not (Test-Path $polPath)) { New-Item -Path $polPath -Force | Out-Null }
        Set-ItemProperty -Path $polPath -Name "DisableTaskMgr" -Value 1 -Type DWord
        Set-ItemProperty -Path $polPath -Name "DisableChangePassword" -Value 1 -Type DWord
        Set-ItemProperty -Path $polPath -Name "DisableRegistryTools" -Value 1 -Type DWord
        Set-ItemProperty -Path $polPath -Name "DisableLockWorkstation" -Value 1 -Type DWord
    }
}
else {
    Write-Host "[7/7] OS Registry politikaları -SkipOsPolicyHardening ile atlandı." -ForegroundColor Gray
}

Write-Host "`nServis başlatılıyor..." -ForegroundColor Cyan
Start-Service -Name $ServiceName
Start-Sleep -Seconds 2
$svcStatus = (Get-Service -Name $ServiceName).Status
Write-Host "HomeworkGuardService Durumu: $svcStatus" -ForegroundColor Green
