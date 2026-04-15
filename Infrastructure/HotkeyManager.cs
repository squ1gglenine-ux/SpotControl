using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;

namespace SpotCont.Infrastructure;

[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 0x0001,
    Control = 0x0002,
    Shift = 0x0004,
    Windows = 0x0008
}

public sealed class HotkeyManager : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;

    private readonly HotkeyModifiers _modifiers;
    private readonly int _virtualKey;
    private readonly LowLevelKeyboardProc _hookProcedure;
    private IntPtr _hookHandle;
    private bool _isChordActive;

    public HotkeyManager(HotkeyModifiers modifiers, Key key, int id = 2026)
    {
        _modifiers = modifiers;
        _virtualKey = KeyInterop.VirtualKeyFromKey(key);
        _hookProcedure = HookCallback;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProcedure, GetModuleHandle(Process.GetCurrentProcess().MainModule?.ModuleName), 0);

        if (_hookHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to register low-level hotkey hook.");
        }
    }

    public event EventHandler? HotkeyPressed;

    public void Dispose()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code < 0)
        {
            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        var message = wParam.ToInt32();
        var keyboardData = Marshal.PtrToStructure<KbdllHookStruct>(lParam);
        var pressedKey = keyboardData.vkCode;

        if (message is WmKeyUp or WmSysKeyUp)
        {
            if (pressedKey == _virtualKey || !AreModifiersPressed())
            {
                _isChordActive = false;
            }

            if (pressedKey == _virtualKey && MatchesModifiers())
            {
                return (IntPtr)1;
            }

            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        if (message is not WmKeyDown and not WmSysKeyDown)
        {
            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        if (pressedKey != _virtualKey || !MatchesModifiers())
        {
            return CallNextHookEx(_hookHandle, code, wParam, lParam);
        }

        if (!_isChordActive)
        {
            _isChordActive = true;
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null)
            {
                HotkeyPressed?.Invoke(this, EventArgs.Empty);
            }
            else
            {
                dispatcher.BeginInvoke(() => HotkeyPressed?.Invoke(this, EventArgs.Empty));
            }
        }

        return (IntPtr)1;
    }

    private bool MatchesModifiers()
    {
        if ((_modifiers & HotkeyModifiers.Alt) != 0 && !IsKeyPressed(Key.LeftAlt, Key.RightAlt))
        {
            return false;
        }

        if ((_modifiers & HotkeyModifiers.Control) != 0 && !IsKeyPressed(Key.LeftCtrl, Key.RightCtrl))
        {
            return false;
        }

        if ((_modifiers & HotkeyModifiers.Shift) != 0 && !IsKeyPressed(Key.LeftShift, Key.RightShift))
        {
            return false;
        }

        if ((_modifiers & HotkeyModifiers.Windows) != 0 &&
            (GetAsyncKeyState(VkLWin) & 0x8000) == 0 &&
            (GetAsyncKeyState(VkRWin) & 0x8000) == 0)
        {
            return false;
        }

        return true;
    }

    private bool AreModifiersPressed()
    {
        return ((_modifiers & HotkeyModifiers.Alt) == 0 || IsKeyPressed(Key.LeftAlt, Key.RightAlt)) &&
               ((_modifiers & HotkeyModifiers.Control) == 0 || IsKeyPressed(Key.LeftCtrl, Key.RightCtrl)) &&
               ((_modifiers & HotkeyModifiers.Shift) == 0 || IsKeyPressed(Key.LeftShift, Key.RightShift)) &&
               ((_modifiers & HotkeyModifiers.Windows) == 0 ||
                (GetAsyncKeyState(VkLWin) & 0x8000) != 0 ||
                (GetAsyncKeyState(VkRWin) & 0x8000) != 0);
    }

    private static bool IsKeyPressed(Key first, Key second)
    {
        return (GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(first)) & 0x8000) != 0 ||
               (GetAsyncKeyState(KeyInterop.VirtualKeyFromKey(second)) & 0x8000) != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    private delegate IntPtr LowLevelKeyboardProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdllHookStruct
    {
        public int vkCode;
        public int scanCode;
        public int flags;
        public int time;
        public IntPtr dwExtraInfo;
    }
}
