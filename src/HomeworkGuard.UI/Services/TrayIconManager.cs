using System.Drawing;
using System.Drawing.Drawing2D;
using HomeworkGuard.Core.Models;
using WinForms = System.Windows.Forms;

namespace HomeworkGuard.UI.Services;

/// <summary>
/// Manages the Windows notification area (system tray / hidden icons) icon so HomeworkGuard
/// stays accessible during MaintenanceMode or UnlockedFreeTime without cluttering the desktop.
/// </summary>
public sealed class TrayIconManager : IDisposable
{
    private WinForms.NotifyIcon? _notifyIcon;
    private WinForms.ToolStripMenuItem? _statusMenuItem;
    private WinForms.ToolStripMenuItem? _resumeProtectionItem;
    private LockState _currentState = LockState.HomeworkRequired;
    private Icon? _currentIcon;

    public event Action? ResumeProtectionClicked;
    public event Action? OpenAdminConsoleClicked;
    public event Action? ForceLockClicked;

    public void Show(LockState state, string statusSummary)
    {
        EnsureCreated();
        UpdateState(state, statusSummary);
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = true;
        }
    }

    public void Hide()
    {
        if (_notifyIcon is not null)
        {
            _notifyIcon.Visible = false;
        }
    }

    public void UpdateState(LockState state, string statusSummary)
    {
        if (_notifyIcon is null) return;

        bool stateChanged = _currentState != state || _currentIcon is null;
        _currentState = state;

        if (stateChanged)
        {
            var oldIcon = _currentIcon;
            _currentIcon = CreateShieldIcon(state);
            _notifyIcon.Icon = _currentIcon;
            oldIcon?.Dispose();
        }

        string shortLabel = state switch
        {
            LockState.MaintenanceMode => "Bakım Modu (Tam Kontrol)",
            LockState.UnlockedFreeTime => "Erişim Açık",
            _ => "Kilit Aktif"
        };

        string fullTip = $"HomeworkGuard • {shortLabel}";
        _notifyIcon.Text = fullTip.Length > 63 ? fullTip[..63] : fullTip;

        if (_statusMenuItem is not null)
        {
            string menuDesc = string.IsNullOrWhiteSpace(statusSummary) ? shortLabel : statusSummary;
            _statusMenuItem.Text = menuDesc.Length > 55 ? menuDesc[..55] + "..." : menuDesc;
        }

        if (_resumeProtectionItem is not null)
        {
            _resumeProtectionItem.Visible = state == LockState.MaintenanceMode;
        }
    }

    private void EnsureCreated()
    {
        if (_notifyIcon is not null) return;

        var menu = new WinForms.ContextMenuStrip();
        _statusMenuItem = new WinForms.ToolStripMenuItem("HomeworkGuard") { Enabled = false };
        _resumeProtectionItem = new WinForms.ToolStripMenuItem("Korumayı Yeniden Devreye Al", null, (_, _) => ResumeProtectionClicked?.Invoke())
        {
            Font = new Font("Segoe UI", 9F, FontStyle.Bold),
            Visible = false
        };
        var adminConsoleItem = new WinForms.ToolStripMenuItem("Yönetici Konsolunu Aç...", null, (_, _) => OpenAdminConsoleClicked?.Invoke());
        var forceLockItem = new WinForms.ToolStripMenuItem("Bilgisayarı Hemen Kilitle...", null, (_, _) => ForceLockClicked?.Invoke());

        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(_resumeProtectionItem);
        menu.Items.Add(adminConsoleItem);
        menu.Items.Add(forceLockItem);

        _currentIcon = CreateShieldIcon(_currentState);
        _notifyIcon = new WinForms.NotifyIcon
        {
            Icon = _currentIcon,
            ContextMenuStrip = menu,
            Text = "HomeworkGuard",
            Visible = false
        };
        _notifyIcon.DoubleClick += (_, _) =>
        {
            if (_currentState == LockState.MaintenanceMode)
            {
                ResumeProtectionClicked?.Invoke();
            }
            else
            {
                OpenAdminConsoleClicked?.Invoke();
            }
        };
    }

    private static Icon CreateShieldIcon(LockState state)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Color fill = state switch
            {
                LockState.MaintenanceMode => Color.FromArgb(14, 116, 144),
                LockState.UnlockedFreeTime => Color.FromArgb(13, 148, 136),
                _ => Color.FromArgb(180, 83, 9)
            };

            Color stroke = state switch
            {
                LockState.MaintenanceMode => Color.FromArgb(56, 189, 248),
                LockState.UnlockedFreeTime => Color.FromArgb(94, 234, 212),
                _ => Color.FromArgb(251, 191, 36)
            };

            using var path = new GraphicsPath();
            path.AddPolygon([
                new PointF(16, 2),
                new PointF(28, 6.5f),
                new PointF(28, 15.5f),
                new PointF(24, 23.5f),
                new PointF(16, 29.5f),
                new PointF(8, 23.5f),
                new PointF(4, 15.5f),
                new PointF(4, 6.5f)
            ]);

            using var brush = new SolidBrush(fill);
            using var pen = new Pen(stroke, 2.2f);
            g.FillPath(brush, path);
            g.DrawPath(pen, path);

            using var dotBrush = new SolidBrush(Color.White);
            g.FillEllipse(dotBrush, 12.5f, 12.5f, 7f, 7f);
        }

        IntPtr hIcon = bmp.GetHicon();
        return Icon.FromHandle(hIcon);
    }

    public void Dispose()
    {
        Hide();
        if (_notifyIcon is not null)
        {
            _notifyIcon.ContextMenuStrip?.Dispose();
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        _currentIcon?.Dispose();
        _currentIcon = null;
    }
}
