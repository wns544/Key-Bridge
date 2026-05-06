using System.Runtime.InteropServices;
using System.Windows.Input;

namespace KeyboardPadBridge.Services;

public sealed class GlobalInputHookService : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const int WmXButtonDown = 0x020B;
    private const int WmXButtonUp = 0x020C;
    private const int WmMouseHWheel = 0x020E;
    private const int VkEscape = 0x1B;
    private const int VkMenu = 0x12;
    private const int VkControl = 0x11;
    private const int VkLControl = 0xA2;
    private const int VkRControl = 0xA3;
    private const int VkLMenu = 0xA4;
    private const int VkRMenu = 0xA5;
    private const int VkLWin = 0x5B;
    private const int VkRWin = 0x5C;
    private const int VkF8 = 0x77;
    private const int VkV = 0x56;
    private const int VkCapital = 0x14;

    private readonly LowLevelProc keyboardProc;
    private readonly LowLevelProc mouseProc;
    private readonly HashSet<int> downVirtualKeys = [];
    private readonly HashSet<int> suppressedChordKeyUps = [];
    private IntPtr keyboardHook;
    private IntPtr mouseHook;

    public GlobalInputHookService()
    {
        keyboardProc = KeyboardHookCallback;
        mouseProc = MouseHookCallback;
    }

    public event EventHandler<GlobalKeyEventArgs>? KeyChanged;

    public event EventHandler<GlobalPointerEventArgs>? PointerMoved;

    public event EventHandler? EmergencyStopRequested;

    public event EventHandler? BridgeToggleRequested;

    public event EventHandler? MouseSignalToggleRequested;

    public event EventHandler? ClipboardTypingRequested;

    public bool IsRunning => keyboardHook != IntPtr.Zero;

    public bool SuppressForwardedKeys { get; set; } = true;

    public bool AlwaysSuppressWindowsKeyShortcuts { get; set; }

    public bool EnableClipboardTypingShortcut { get; set; }

    public bool SuppressForwardedPointerEvents { get; set; }

    public Func<bool>? ShouldCaptureForwardedInput { get; set; }

    public bool CapturePointerEvents
    {
        get => mouseHook != IntPtr.Zero;
        set
        {
            if (value)
            {
                StartMouseHook();
                return;
            }

            StopMouseHook();
        }
    }

    public void Start()
    {
        if (keyboardHook != IntPtr.Zero)
        {
            return;
        }

        keyboardHook = SetHook(WhKeyboardLl, keyboardProc);
    }

    public void Stop()
    {
        if (keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(keyboardHook);
            keyboardHook = IntPtr.Zero;
        }

        StopMouseHook();
        ResetPressedKeyState();
    }

    public void Dispose()
    {
        Stop();
    }

    private static IntPtr SetHook(int hookType, LowLevelProc proc)
    {
        using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
        using var currentModule = currentProcess.MainModule;
        var moduleHandle = currentModule is null ? IntPtr.Zero : GetModuleHandle(currentModule.ModuleName);
        return SetWindowsHookEx(hookType, proc, moduleHandle, 0);
    }

    public void ResetPressedKeyState()
    {
        downVirtualKeys.Clear();
        suppressedChordKeyUps.Clear();
    }

    private void StartMouseHook()
    {
        if (mouseHook != IntPtr.Zero)
        {
            return;
        }

        mouseHook = SetHook(WhMouseLl, mouseProc);
    }

    private void StopMouseHook()
    {
        if (mouseHook == IntPtr.Zero)
        {
            return;
        }

        UnhookWindowsHookEx(mouseHook);
        mouseHook = IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsKeyboardMessage(wParam))
        {
            var hookInfo = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
            var virtualKey = (int)hookInfo.VkCode;
            var isDown = wParam == WmKeyDown || wParam == WmSysKeyDown;

            if (isDown)
            {
                downVirtualKeys.Add(virtualKey);
            }

            if (!isDown && suppressedChordKeyUps.Remove(virtualKey))
            {
                downVirtualKeys.Remove(virtualKey);
                return 1;
            }

            if (isDown && IsEmergencyStopChord(virtualKey))
            {
                EmergencyStopRequested?.Invoke(this, EventArgs.Empty);
                return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
            }

            if (isDown && IsBridgeToggleChord(virtualKey))
            {
                suppressedChordKeyUps.Add(virtualKey);
                BridgeToggleRequested?.Invoke(this, EventArgs.Empty);
                return 1;
            }

            if (isDown && IsMouseSignalToggleChord(virtualKey))
            {
                suppressedChordKeyUps.Add(virtualKey);
                MouseSignalToggleRequested?.Invoke(this, EventArgs.Empty);
                return 1;
            }

            if (EnableClipboardTypingShortcut && ShouldCaptureInput() && isDown && IsClipboardTypingChord(virtualKey))
            {
                suppressedChordKeyUps.Add(virtualKey);
                ClipboardTypingRequested?.Invoke(this, EventArgs.Empty);
                return 1;
            }

            if (ShouldSuppressWindowsKeyShortcut(virtualKey))
            {
                if (!isDown)
                {
                    downVirtualKeys.Remove(virtualKey);
                }

                return 1;
            }

            var key = KeyInterop.KeyFromVirtualKey(virtualKey);
            KeyChanged?.Invoke(this, new GlobalKeyEventArgs(key, virtualKey, isDown));

            if (!isDown)
            {
                downVirtualKeys.Remove(virtualKey);
            }

            if (SuppressForwardedKeys && ShouldCaptureInput())
            {
                return 1;
            }
        }

        return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsKeyboardMessage(IntPtr message)
    {
        return message == WmKeyDown
            || message == WmKeyUp
            || message == WmSysKeyDown
            || message == WmSysKeyUp;
    }

    private bool ShouldSuppressWindowsKeyShortcut(int virtualKey)
    {
        if (!AlwaysSuppressWindowsKeyShortcuts || !ShouldCaptureInput())
        {
            return false;
        }

        if (virtualKey is VkLWin or VkRWin)
        {
            return true;
        }

        var leftWinDown = downVirtualKeys.Contains(VkLWin) || (GetAsyncKeyState(VkLWin) & 0x8000) != 0;
        var rightWinDown = downVirtualKeys.Contains(VkRWin) || (GetAsyncKeyState(VkRWin) & 0x8000) != 0;
        return leftWinDown || rightWinDown;
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsPointerMessage(wParam))
        {
            var hookInfo = Marshal.PtrToStructure<MouseLlHookStruct>(lParam);
            PointerMoved?.Invoke(this, new GlobalPointerEventArgs(hookInfo.Point.X, hookInfo.Point.Y, wParam, hookInfo.MouseData));

            if (SuppressForwardedPointerEvents && ShouldCaptureInput())
            {
                return 1;
            }
        }

        return CallNextHookEx(mouseHook, nCode, wParam, lParam);
    }

    private static bool IsPointerMessage(IntPtr message)
    {
        return message == WmMouseMove
            || message == WmLButtonDown
            || message == WmLButtonUp
            || message == WmRButtonDown
            || message == WmRButtonUp
            || message == WmMButtonDown
            || message == WmMButtonUp
            || message == WmMouseWheel
            || message == WmMouseHWheel
            || message == WmXButtonDown
            || message == WmXButtonUp;
    }

    private bool IsEmergencyStopChord(int virtualKey)
    {
        if (virtualKey != VkEscape)
        {
            return false;
        }

        return IsControlDown() && IsAltDown();
    }

    private bool ShouldCaptureInput()
    {
        try
        {
            return ShouldCaptureForwardedInput?.Invoke() ?? true;
        }
        catch
        {
            return false;
        }
    }

    private bool IsBridgeToggleChord(int virtualKey)
    {
        if (virtualKey != VkCapital)
        {
            return false;
        }

        return IsControlDown() && !IsAltDown();
    }

    private bool IsMouseSignalToggleChord(int virtualKey)
    {
        if (virtualKey != VkF8)
        {
            return false;
        }

        return IsControlDown() && !IsAltDown();
    }

    private bool IsClipboardTypingChord(int virtualKey)
    {
        if (virtualKey != VkV)
        {
            return false;
        }

        return IsControlDown() && !IsAltDown();
    }

    private bool IsControlDown()
    {
        return downVirtualKeys.Contains(VkControl)
            || downVirtualKeys.Contains(VkLControl)
            || downVirtualKeys.Contains(VkRControl)
            || (GetAsyncKeyState(VkControl) & 0x8000) != 0
            || (GetAsyncKeyState(VkLControl) & 0x8000) != 0
            || (GetAsyncKeyState(VkRControl) & 0x8000) != 0;
    }

    private bool IsShiftDown()
    {
        const int vkShift = 0x10;
        const int vkLShift = 0xA0;
        const int vkRShift = 0xA1;

        return downVirtualKeys.Contains(vkShift)
            || downVirtualKeys.Contains(vkLShift)
            || downVirtualKeys.Contains(vkRShift)
            || (GetAsyncKeyState(vkShift) & 0x8000) != 0
            || (GetAsyncKeyState(vkLShift) & 0x8000) != 0
            || (GetAsyncKeyState(vkRShift) & 0x8000) != 0;
    }

    private bool IsAltDown()
    {
        return downVirtualKeys.Contains(VkMenu)
            || downVirtualKeys.Contains(VkLMenu)
            || downVirtualKeys.Contains(VkRMenu)
            || (GetAsyncKeyState(VkMenu) & 0x8000) != 0
            || (GetAsyncKeyState(VkLMenu) & 0x8000) != 0
            || (GetAsyncKeyState(VkRMenu) & 0x8000) != 0;
    }

    private delegate IntPtr LowLevelProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct KbdLlHookStruct
    {
        public readonly uint VkCode;
        public readonly uint ScanCode;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct MouseLlHookStruct
    {
        public readonly Point Point;
        public readonly uint MouseData;
        public readonly uint Flags;
        public readonly uint Time;
        public readonly IntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Point
    {
        public readonly int X;
        public readonly int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}

public sealed record CapturedKey(Key Key, int VirtualKey);

public sealed record GlobalKeyEventArgs(Key Key, int VirtualKey, bool IsDown)
{
    public CapturedKey CapturedKey => new(Key, VirtualKey);
}

public sealed record GlobalPointerEventArgs(int X, int Y, IntPtr Message, uint MouseData);
