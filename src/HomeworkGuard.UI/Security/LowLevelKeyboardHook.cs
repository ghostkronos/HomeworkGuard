using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace HomeworkGuard.UI.Security;

/// <summary>
/// Low-Level Keyboard Hook (WH_KEYBOARD_LL) active ONLY when IS_DEV_SANDBOX_MODE == false.
/// Swallows Alt+Tab, Alt+F4, Win keys, Ctrl+Esc, Ctrl+Shift+Esc, Alt+Esc,
/// while allowing the secret Admin shortcut: Ctrl + Alt + Shift + F12.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private const int VK_TAB = 0x09;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_F4 = 0x73;
    private const int VK_F12 = 0x7B;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_APPS = 0x5D;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_SHIFT = 0x10;
    private const int VK_LSHIFT = 0xA0;
    private const int VK_RSHIFT = 0xA1;
    private const int VK_MENU = 0x12; // Alt
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    private static readonly string HookLogPath = Path.Combine(Path.GetTempPath(), "HomeworkGuard_UI_hook.log");

    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hookId = IntPtr.Zero;
    private bool _ctrlPressed;
    private bool _altPressed;
    private bool _shiftPressed;

    public event Action? AdminShortcutPressed;

    public LowLevelKeyboardHook()
    {
        _proc = HookCallback;
    }

    public bool IsInstalled => _hookId != IntPtr.Zero;

    public void Install()
    {
        if (_hookId != IntPtr.Zero) return;

        using Process curProcess = Process.GetCurrentProcess();
        using ProcessModule? curModule = curProcess.MainModule;
        IntPtr hMod = GetModuleHandle(curModule?.ModuleName);
        _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, hMod, 0);
        int err1 = Marshal.GetLastWin32Error();

        if (_hookId == IntPtr.Zero)
        {
            _hookId = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, IntPtr.Zero, 0);
            int err2 = Marshal.GetLastWin32Error();
            LogHook($"Install fallback(Zero): hook={_hookId} err1={err1} err2={err2} mod={curModule?.ModuleName} tid={GetCurrentThreadId()} sid={curProcess.SessionId}");
        }
        else
        {
            LogHook($"Install ok(hMod): hook={_hookId} err1={err1} mod={curModule?.ModuleName} tid={GetCurrentThreadId()} sid={curProcess.SessionId}");
        }
    }

    public void Uninstall()
    {
        if (_hookId == IntPtr.Zero) return;
        bool ok = UnhookWindowsHookEx(_hookId);
        LogHook($"Uninstall: hook={_hookId} ok={ok}");
        _hookId = IntPtr.Zero;
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = wParam.ToInt32();
                bool isKeyDown = msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN;
                bool isKeyEvent = isKeyDown || msg == WM_KEYUP || msg == WM_SYSKEYUP;

                if (isKeyEvent)
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)kb.vkCode;

                    if (vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL) _ctrlPressed = isKeyDown;
                    if (vk is VK_MENU or VK_LMENU or VK_RMENU) _altPressed = isKeyDown;
                    if (vk is VK_SHIFT or VK_LSHIFT or VK_RSHIFT) _shiftPressed = isKeyDown;

                    bool altDown = _altPressed || (kb.flags & 0x20) != 0 || IsKeyDown(VK_MENU) || IsKeyDown(VK_LMENU) || IsKeyDown(VK_RMENU);
                    bool ctrlDown = _ctrlPressed || IsKeyDown(VK_CONTROL) || IsKeyDown(VK_LCONTROL) || IsKeyDown(VK_RCONTROL);
                    bool shiftDown = _shiftPressed || IsKeyDown(VK_SHIFT) || IsKeyDown(VK_LSHIFT) || IsKeyDown(VK_RSHIFT);

                    // Allow secret Admin combo: Ctrl + Alt + Shift + F12
                    if (vk == VK_F12 && ctrlDown && altDown && shiftDown)
                    {
                        LogHook($"Allow AdminShortcut F12 down={isKeyDown}");
                        if (isKeyDown)
                        {
                            AdminShortcutPressed?.Invoke();
                        }
                        return (IntPtr)1;
                    }

                    // Block Windows keys & Context Menu key
                    if (vk is VK_LWIN or VK_RWIN or VK_APPS)
                    {
                        LogHook($"Blocked Win/Apps vk=0x{vk:X2} msg=0x{msg:X}");
                        return (IntPtr)1;
                    }

                    // Block Alt+Tab, Alt+F4, Alt+Esc
                    if (altDown && vk is VK_TAB or VK_F4 or VK_ESCAPE)
                    {
                        LogHook($"Blocked Alt+0x{vk:X2}");
                        return (IntPtr)1;
                    }

                    // Block Ctrl+Esc, Ctrl+Shift+Esc
                    if (ctrlDown && vk == VK_ESCAPE)
                    {
                        LogHook($"Blocked Ctrl+Esc");
                        return (IntPtr)1;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogHook($"HookCallback exception: {ex.Message}");
        }

        return CallNextHookEx(_hookId, nCode, wParam, lParam);
    }

    private static void LogHook(string message)
    {
        try
        {
            File.AppendAllText(HookLogPath, $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch
        {
            // Ignore logging failures in hook path
        }
    }

    private static bool IsKeyDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public void Dispose() => Uninstall();
}
