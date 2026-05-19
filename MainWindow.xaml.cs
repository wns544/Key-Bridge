using KeyboardPadBridge.Models;
using KeyboardPadBridge.Services;
using System.IO;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace KeyboardPadBridge;

public partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly record struct ClipboardTypingToken(bool UsesKoreanInputSource, string Text);
    private const int ClipboardTypingStartDelayMs = 350;
    private const int ClipboardCharacterHoldMs = 25;
    private const int ClipboardCharacterReleaseMs = 15;
    private const int InputSourceToggleHoldMs = 100;
    private const int InputSourceToggleSettleMs = 450;

    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmInput = 0x00FF;
    private const int WmHotKey = 0x0312;
    private const int RidInput = 0x10000003;
    private const int RidevInputSink = 0x00000100;
    private const int HotKeyClipboardText = 3001;
    private const int HotKeyClipboardImage = 3002;
    private const int HotKeyClipboardTextFallback = 3003;
    private const int HotKeyClipboardImageFallback = 3004;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModNoRepeat = 0x4000;
    private const int RawInputTypeMouse = 0;
    private const uint MouseEventFLeftUp = 0x0004;
    private const uint MouseEventFRightUp = 0x0010;
    private const uint MouseEventFMiddleUp = 0x0040;
    private const ushort RawMouseLeftButtonDown = 0x0001;
    private const ushort RawMouseLeftButtonUp = 0x0002;
    private const ushort RawMouseRightButtonDown = 0x0004;
    private const ushort RawMouseRightButtonUp = 0x0008;
    private const ushort RawMouseMiddleButtonDown = 0x0010;
    private const ushort RawMouseMiddleButtonUp = 0x0020;
    private const ushort RawMouseWheel = 0x0400;
    private const ushort RawMouseButton4Down = 0x0040;
    private const ushort RawMouseButton4Up = 0x0080;
    private const ushort RawMouseButton5Down = 0x0100;
    private const ushort RawMouseButton5Up = 0x0200;
    private const int MouseWheelDelta = 120;
    private const byte KeyEventFKeyUp = 0x02;
    private const byte VkControl = 0x11;
    private const byte VkLControl = 0xA2;
    private const byte VkRControl = 0xA3;
    private const byte VkShift = 0x10;
    private const byte VkLShift = 0xA0;
    private const byte VkRShift = 0xA1;
    private const byte VkMenu = 0x12;
    private const byte VkLMenu = 0xA4;
    private const byte VkRMenu = 0xA5;
    private const byte VkLWin = 0x5B;
    private const byte VkRWin = 0x5C;
    private const int VkLButton = 0x01;
    private const int VkRButton = 0x02;
    private const int VkMButton = 0x04;
    private const int VkF3 = 0x72;
    private const int VkF4 = 0x73;
    private const int VkI = 0x49;
    private const int VkV = 0x56;
    private const ushort ConsumerMute = 0x00E2;
    private const ushort ConsumerVolumeIncrement = 0x00E9;
    private const ushort ConsumerVolumeDecrement = 0x00EA;
    private const ushort ConsumerBrowserBack = 0x0224;
    private const ushort ConsumerBrowserForward = 0x0225;
    private const double MouseScaleX = 0.8;
    private const double MouseScaleY = 0.8;
    private const int MouseSendIntervalMs = 8;
    private const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunRegistryName = "KeyBridge";

    private readonly IHidBridgeService bridgeService = new BluetoothHidBridgeService();
    private readonly GlobalInputHookService inputHookService = new();
    private readonly BluetoothCapabilityProbe bluetoothCapabilityProbe = new();
    private readonly ScreenshotShareService screenshotShareService = new();
    private readonly DeviceProfile activeDevice = new("BLE HID Peer", "Bluetooth HID", "Alt+Q");
    private readonly Dictionary<int, CapturedKey> pressedKeys = [];
    private readonly object mouseStateLock = new();
    private DateTime lastWheelEvent = DateTime.MinValue;
    private bool isModifierStickyActive;
    private DateTime lastPointerEvent = DateTime.MinValue;
    private DateTime lastPointerLogEvent = DateTime.MinValue;
    private int pendingMouseDeltaX;
    private int pendingMouseDeltaY;
    private bool mouseSendLoopRunning;
    private bool isMouseSignalEnabled;
    private bool isExitRequested;
    private bool hasShownTrayTip;
    private bool isClipboardTypingInProgress;
    private bool hasShownBridgeConnectedToast;
    private bool hasShownBridgeConnectionFailureToast;
    private CancellationTokenSource? clipboardTypingCancellation;
    private CancellationTokenSource? bridgeConnectionToastCancellation;
    private CancellationTokenSource? mouseSendLoopCancellation;
    private byte mouseButtons;
    private Forms.NotifyIcon? trayIcon;
    private BridgeStatusToastWindow? bridgeStatusToast;

    public MainWindow()
    {
        InitializeComponent();
        LoadWindowIcon();

        inputHookService.KeyChanged += InputHookService_KeyChanged;
        inputHookService.EmergencyStopRequested += InputHookService_EmergencyStopRequested;
        inputHookService.BridgeToggleRequested += InputHookService_BridgeToggleRequested;
        inputHookService.MouseSignalToggleRequested += InputHookService_MouseSignalToggleRequested;
        inputHookService.EmojiPickerRequested += InputHookService_EmojiPickerRequested;
        inputHookService.ClipboardTypingRequested += InputHookService_ClipboardTypingRequested;
        inputHookService.ClipboardTypingWithInputSourceToggleRequested += InputHookService_ClipboardTypingWithInputSourceToggleRequested;
        inputHookService.ClipboardTypingCancelRequested += InputHookService_ClipboardTypingCancelRequested;
        inputHookService.ScreenshotRequested += InputHookService_ScreenshotRequested;
        inputHookService.ClipboardImageShareRequested += InputHookService_ClipboardImageShareRequested;
        bridgeService.DiagnosticMessage += BridgeService_DiagnosticMessage;
        bridgeService.MouseSubscriberChanged += BridgeService_MouseSubscriberChanged;
        bridgeService.ConnectionStateChanged += BridgeService_ConnectionStateChanged;
        inputHookService.SuppressForwardedKeys = false;
        inputHookService.AlwaysSuppressWindowsKeyShortcuts = false;
        inputHookService.ShouldCaptureForwardedInput = () => bridgeService.IsRunning && (bridgeService.HasKeyboardSubscriber || bridgeService.HasMouseSubscriber);
        UpdatePointerCapture();
        inputHookService.Start();
        Closing += Window_Closing;
        StateChanged += Window_StateChanged;
        InitializeTrayIcon();
        StartWithWindowsCheckBox.IsChecked = IsStartWithWindowsEnabled();
        MouseSignalCheckBox.IsChecked = true;

        DataContext = this;
        AddActivity("시스템", "앱이 준비되었습니다.");
        AddActivity("시스템", "Alt+Q로 브릿지를 시작하거나 중지할 수 있습니다.");
        _ = InitializeScreenshotShareAsync();
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public System.Collections.ObjectModel.ObservableCollection<ActivityEvent> ActivityEvents { get; } = [];

    private async void BridgeToggleButton_Click(object sender, RoutedEventArgs e)
    {
        if (bridgeService.IsRunning)
        {
            await StopBridgeAsync("브릿지를 중지했습니다.");
            return;
        }

        await StartBridgeAsync();
    }

    private async void AdvertiseButton_Click(object sender, RoutedEventArgs e)
    {
        AdvertiseButton.IsEnabled = false;

        try
        {
            AddActivity("브릿지", "iPad가 이 PC를 다시 찾을 수 있도록 검색을 재시작합니다.");

            if (bridgeService.IsRunning)
            {
                await StopBridgeAsync("기기 다시 찾기를 위해 기존 BLE HID 세션을 중지했습니다.");
                await Task.Delay(350);
            }

            await StartBridgeAsync();
            AddActivity("브릿지", "검색 준비가 끝났습니다. iPad에서는 먼저 '액세서리'를 찾고, 연결 후 이 PC의 블루투스 이름으로 바뀔 수 있습니다.");
        }
        finally
        {
            AdvertiseButton.IsEnabled = true;
        }
    }

    private async Task StartBridgeAsync()
    {
        try
        {
            await bridgeService.StartAsync(activeDevice);
            await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());
            pressedKeys.Clear();
            ResetMouseState();
            ReleaseLocalMouseButtons();
            inputHookService.SuppressForwardedKeys = SuppressKeysCheckBox.IsChecked == true;
            inputHookService.AlwaysSuppressWindowsKeyShortcuts = true;
            inputHookService.EnableClipboardTypingShortcut = false;
            inputHookService.SuppressForwardedPointerEvents = isMouseSignalEnabled;
            UpdatePointerCapture();

            BackendStateText.Text = "BLE HID";
            RemoteDeviceText.Text = "페어링 중입니다. iPad에서 '액세서리'를 찾으세요. 연결 후 이름이 바뀔 수 있습니다.";
            AddActivity("브릿지", "BLE HID 검색을 시작했습니다. iPad에서 먼저 '액세서리'를 선택하세요. 연결 후 이 PC의 블루투스 이름으로 바뀔 수 있습니다.");
            RefreshStatus();
            BeginBridgeConnectionFeedbackWindow();
        }
        catch (Exception ex)
        {
            await bridgeService.StopAsync();
            AddActivity("브릿지", $"{ex.GetType().Name}: {ex.Message}");
            BackendStateText.Text = "실패";
            RefreshStatus();
        }
    }

    private async Task StopBridgeAsync(string message)
    {
        StopMouseCaptureImmediately();
        pressedKeys.Clear();
        await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());
        inputHookService.SuppressForwardedKeys = false;
        inputHookService.AlwaysSuppressWindowsKeyShortcuts = false;
        inputHookService.EnableClipboardTypingShortcut = false;
        inputHookService.ResetPressedKeyState();
        ReleaseLocalModifierKeys();
        await bridgeService.SendMouseReportAsync(activeDevice, 0, 0, 0, 0, 0);
        ReleaseLocalMouseButtons();
        await bridgeService.StopAsync();
        CancelBridgeConnectionFeedback();
        hasShownBridgeConnectedToast = false;
        hasShownBridgeConnectionFailureToast = false;

        AddActivity("브릿지", message);
        RefreshStatus();
        ShowBridgeStatusToast("Key Bridge", "iPad", false, "연결 해제");
    }

    private void ClearLogButton_Click(object sender, RoutedEventArgs e)
    {
        ActivityEvents.Clear();
        AddActivity("시스템", "로그를 지웠습니다.");
    }

    private void CopyLogsButton_Click(object sender, RoutedEventArgs e)
    {
        if (ActivityEvents.Count == 0)
        {
            AddActivity("시스템", "복사할 로그가 없습니다.");
            return;
        }

        var logBuilder = new StringBuilder();

        foreach (var activityEvent in ActivityEvents.Reverse())
        {
            logBuilder
                .Append(activityEvent.Timestamp.ToString("HH:mm:ss"))
                .Append('\t')
                .Append(activityEvent.Channel)
                .Append('\t')
                .AppendLine(activityEvent.Message);
        }

        try
        {
            System.Windows.Clipboard.SetText(logBuilder.ToString().TrimEnd());
            AddActivity("시스템", $"로그 {ActivityEvents.Count}개를 클립보드에 복사했습니다.");
        }
        catch (Exception ex)
        {
            AddActivity("시스템", $"클립보드 복사 실패: {ex.Message}");
        }
    }

    private async void ProbeButton_Click(object sender, RoutedEventArgs e)
    {
        ProbeButton.IsEnabled = false;
        AddActivity("진단", "블루투스 기능 진단을 시작했습니다.");

        try
        {
            var result = await bluetoothCapabilityProbe.RunAsync();
            BackendStateText.Text = result.Summary;

            foreach (var message in result.Messages)
            {
                AddActivity("Probe", message);
            }
        }
        catch (Exception ex)
        {
            BackendStateText.Text = "진단 실패";
            AddActivity("진단", $"{ex.GetType().Name}: {ex.Message}");
        }
        finally
        {
            ProbeButton.IsEnabled = true;
        }
    }

    private async void MouseTestButton_Click(object sender, RoutedEventArgs e)
    {
        MouseTestButton.IsEnabled = false;

        try
        {
            if (!bridgeService.IsRunning)
            {
                AddActivity("마우스", "먼저 브릿지를 시작하고 iPad와 연결한 뒤 마우스 테스트를 실행하세요.");
                return;
            }

            AddActivity("마우스", "마우스 테스트를 시작했습니다. iPad 커서가 작은 사각형으로 움직여야 합니다.");
            await SendMouseTestPatternAsync();
            AddActivity("마우스", "마우스 테스트를 마쳤습니다.");
        }
        finally
        {
            MouseTestButton.IsEnabled = true;
        }
    }

    private async void TypeClipboardButton_Click(object sender, RoutedEventArgs e)
    {
        await TypeClipboardTextAsync();
    }

    private async void ScreenshotButton_Click(object sender, RoutedEventArgs e)
    {
        await CaptureAndShareScreenshotAsync();
    }

    private async void ShareClipboardImageButton_Click(object sender, RoutedEventArgs e)
    {
        await ShareClipboardImageAsync();
    }

    private void SuppressKeysCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        inputHookService.SuppressForwardedKeys = SuppressKeysCheckBox.IsChecked == true;
        AddActivity("System", inputHookService.SuppressForwardedKeys
            ? "브릿지 사용 중 노트북 입력을 차단합니다."
            : "브릿지 사용 중에도 노트북 입력을 통과시킵니다.");
    }

    private async void MouseSignalCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        isMouseSignalEnabled = MouseSignalCheckBox.IsChecked == true;
        if (isMouseSignalEnabled)
        {
            ResetMouseState();
            inputHookService.SuppressForwardedPointerEvents = bridgeService.IsRunning;
            UpdatePointerCapture();
        }
        else
        {
            StopMouseCaptureImmediately();
        }

        if (isMouseSignalEnabled)
        {
            ReleaseLocalMouseButtons();
        }

        if (!isMouseSignalEnabled && bridgeService.IsRunning)
        {
            await bridgeService.SendMouseReportAsync(activeDevice, 0, 0, 0, 0, 0);
            ReleaseLocalMouseButtons();
        }

        MouseSignalSummaryText.Text = isMouseSignalEnabled ? "켜짐" : "꺼짐";
        MouseStateText.Text = bridgeService.IsRunning
            ? isMouseSignalEnabled ? "연결됨" : "꺼짐"
            : "준비됨";
        AddActivity("시스템", isMouseSignalEnabled
            ? "마우스 전송을 켰습니다."
            : "마우스 전송을 껐습니다.");

        if (IsLoaded)
        {
            ShowBridgeStatusToast("마우스", "Mouse", isMouseSignalEnabled);
        }
    }

    private void StartWithWindowsCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        var shouldStartWithWindows = StartWithWindowsCheckBox.IsChecked == true;

        try
        {
            SetStartWithWindows(shouldStartWithWindows);
            AddActivity("시스템", shouldStartWithWindows
                ? "KeyBridge를 윈도우 시작 시 자동 실행합니다."
                : "KeyBridge를 윈도우 시작 시 자동 실행하지 않습니다.");
        }
        catch (Exception ex)
        {
            AddActivity("시스템", $"자동 실행 설정 변경 실패: {ex.Message}");
            StartWithWindowsCheckBox.IsChecked = !shouldStartWithWindows;
        }
    }

    private void InputHookService_KeyChanged(object? sender, GlobalKeyEventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!bridgeService.IsRunning)
            {
                return;
            }

            if (TryGetConsumerControlUsage(e, out var consumerUsage))
            {
                if (e.IsDown)
                {
                    await bridgeService.SendConsumerControlAsync(activeDevice, consumerUsage);
                    AddActivity("키보드", $"{e.Key} -> 소비자 제어 0x{consumerUsage:X4}");
                }

                return;
            }

            if (IsOneShotKeyboardToggle(e.CapturedKey))
            {
                pressedKeys.Remove(e.VirtualKey);

                if (e.IsDown)
                {
                    await bridgeService.SendKeyboardStateAsync(activeDevice, new[] { e.CapturedKey });
                    await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());
                    AddActivity("키보드", $"{e.Key} -> 언어 전환");
                }

                return;
            }

            if (e.IsDown)
            {
                pressedKeys[e.VirtualKey] = e.CapturedKey;
            }
            else
            {
                pressedKeys.Remove(e.VirtualKey);
                
                // Modifier release protection during wheeling (Increased to 500ms for extra stability)
                bool shouldDelayRelease = false;
                lock (mouseStateLock)
                {
                    if (isModifierStickyActive && (DateTime.Now - lastWheelEvent).TotalMilliseconds < 500)
                    {
                        shouldDelayRelease = true;
                    }
                    else
                    {
                        isModifierStickyActive = false;
                    }
                }

                if (shouldDelayRelease && (e.VirtualKey is VkControl or VkLControl or VkRControl or VkShift or VkLShift or VkRShift))
                {
                    AddActivity("시스템", $"휠 동작 보호: {e.Key} 뗌 지연 중 (0.5초 유지)");
                    return; 
                }
            }

            await bridgeService.SendKeyboardStateAsync(activeDevice, pressedKeys.Values.ToList());
            AddActivity("키보드", $"{(e.IsDown ? "누름" : "뗌")} {e.Key}; 유지 중: {HidKeyboardReport.DescribePressedKeys(pressedKeys.Values)}");
        });
    }

    private static bool IsOneShotKeyboardToggle(CapturedKey key)
    {
        return key.VirtualKey is 0x14 or 0x15 or 0xA5
            || key.Key is System.Windows.Input.Key.CapsLock
                or System.Windows.Input.Key.HangulMode
                or System.Windows.Input.Key.KanaMode
                or System.Windows.Input.Key.RightAlt;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var windowHandle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(windowHandle)?.AddHook(WndProc);
        RegisterRawMouseInput(windowHandle);
        RegisterClipboardHotKeys(windowHandle);
    }

    private IntPtr WndProc(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WmInput)
        {
            HandleRawMouseInput(lParam);
        }
        else if (message == WmHotKey)
        {
            var hotKeyId = wParam.ToInt32();
            if (hotKeyId == HotKeyClipboardText)
            {
                _ = TypeClipboardTextAsync();
                handled = true;
            }
            else if (hotKeyId == HotKeyClipboardImage)
            {
                _ = ShareClipboardImageAsync();
                handled = true;
            }
            else if (hotKeyId == HotKeyClipboardImageFallback)
            {
                _ = ShareClipboardImageAsync();
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    private void HandleRawMouseInput(IntPtr rawInputHandle)
    {
        if (!bridgeService.IsRunning || !isMouseSignalEnabled)
        {
            return;
        }

        if (!TryReadRawMouseInput(rawInputHandle, out var rawMouseInput))
        {
            return;
        }

        if (TryGetMouseNavigationShortcut(rawMouseInput.ButtonFlags, out var shortcutReport, out var shortcutDescription))
        {
            _ = SendMouseKeyboardShortcutAsync(shortcutReport, shortcutDescription);
            return;
        }

        var now = DateTime.Now;
        sbyte deltaX;
        sbyte deltaY;
        sbyte wheel;
        byte buttons;
        bool shouldLog;

        lock (mouseStateLock)
        {
            if (rawMouseInput.ButtonFlags == 0 && (now - lastPointerEvent).TotalMilliseconds < 3)
            {
                return;
            }

            if (rawMouseInput.ButtonFlags == 0)
            {
                lastPointerEvent = now;
            }

            UpdateMouseButtons(rawMouseInput.ButtonFlags);

            deltaX = ScaleMouseDelta(rawMouseInput.DeltaX, MouseScaleX);
            deltaY = ScaleMouseDelta(rawMouseInput.DeltaY, MouseScaleY);
            wheel = ScaleMouseWheel(rawMouseInput.ButtonFlags, rawMouseInput.ButtonData);

            if (wheel != 0)
            {
                if ((now - lastWheelEvent).TotalMilliseconds < 15)
                {
                    wheel = 0;
                }
                else
                {
                    lastWheelEvent = now;
                }
            }

            buttons = mouseButtons;

            if (deltaX == 0 && deltaY == 0 && wheel == 0 && rawMouseInput.ButtonFlags == 0)
            {
                return;
            }

            shouldLog = (now - lastPointerLogEvent).TotalMilliseconds >= 250 || rawMouseInput.ButtonFlags != 0;
            if (shouldLog)
            {
                lastPointerLogEvent = now;
            }
        }

        if (rawMouseInput.ButtonFlags != 0)
        {
            _ = SendMouseButtonReportAsync(deltaX, deltaY, buttons, wheel, shouldLog);
            return;
        }

        QueueMouseReport(deltaX, deltaY, buttons, shouldLog);
    }

    private void InputHookService_EmergencyStopRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(async () => await StopBridgeAsync("긴급 중지: Ctrl+Alt+Esc."));
    }

    private void InputHookService_BridgeToggleRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (bridgeService.IsRunning)
            {
                await StopBridgeAsync("Alt+Q로 브릿지를 중지했습니다.");
                return;
            }

            await StartBridgeAsync();
        });
    }

    private void BridgeService_MouseSubscriberChanged(object? sender, bool isConnected)
    {
        if (isConnected)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                ResetMouseState();
                await Task.Delay(700);
                for (var attempt = 0; attempt < 3 && bridgeService.IsRunning; attempt++)
                {
                    await bridgeService.SendPointerAsync(activeDevice, 50, 50);
                    await Task.Delay(300);
                }
                AddActivity("Mouse", "Mouse connected. Re-applied absolute center pointer position.");
            });
        }
    }

    private void BridgeService_ConnectionStateChanged(object? sender, bool isConnected)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!bridgeService.IsRunning)
            {
                return;
            }

            if (isConnected)
            {
                CancelBridgeConnectionFeedback();

                if (!hasShownBridgeConnectedToast)
                {
                    hasShownBridgeConnectedToast = true;
                    hasShownBridgeConnectionFailureToast = false;
                    ShowBridgeStatusToast("iPad", "iPad", true, "연결");
                }

                AddActivity("브릿지", "iPad HID 연결이 확인되었습니다.");
                return;
            }

            if (hasShownBridgeConnectedToast)
            {
                hasShownBridgeConnectedToast = false;
                ShowBridgeStatusToast("iPad", "iPad", false, "연결 끊김");
            }
        });
    }

    private void InputHookService_MouseSignalToggleRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            pressedKeys.Clear();

            if (bridgeService.IsRunning)
            {
                await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());
            }

            MouseSignalCheckBox.IsChecked = !isMouseSignalEnabled;
        });
    }

    private void InputHookService_EmojiPickerRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(async () =>
        {
            if (!bridgeService.IsRunning)
            {
                AddActivity("키보드", "먼저 브릿지를 시작한 뒤 Ctrl+Alt+E로 이모티콘/입력 선택을 열 수 있습니다.");
                return;
            }

            pressedKeys.Clear();
            await bridgeService.SendKeyboardReportAsync(activeDevice, new byte[] { 0x01, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x00 }, "emoji/input picker Ctrl+Space");
            await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());
            AddActivity("키보드", "Ctrl+Alt+E -> 이모티콘/입력 선택");
        });
    }

    private void InputHookService_ClipboardTypingRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() => TypeClipboardTextAsync());
    }

    private void InputHookService_ClipboardTypingWithInputSourceToggleRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() => TypeClipboardTextAsync(switchInputSourceFirst: true));
    }

    private void InputHookService_ClipboardTypingCancelRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(() =>
        {
            if (!isClipboardTypingInProgress)
            {
                return;
            }

            clipboardTypingCancellation?.Cancel();
            AddActivity("Clipboard", "Cancel requested by Esc.");
        });
    }

    private void InputHookService_ScreenshotRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(CaptureAndShareScreenshotAsync);
    }

    private void InputHookService_ClipboardImageShareRequested(object? sender, EventArgs e)
    {
        Dispatcher.InvokeAsync(ShareClipboardImageAsync);
    }

    private void BridgeService_DiagnosticMessage(object? sender, string message)
    {
        Dispatcher.InvokeAsync(() => AddActivity("HID", message));
    }

    private void BeginBridgeConnectionFeedbackWindow()
    {
        CancelBridgeConnectionFeedback();
        hasShownBridgeConnectedToast = false;
        hasShownBridgeConnectionFailureToast = false;

        var cancellation = new CancellationTokenSource();
        bridgeConnectionToastCancellation = cancellation;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(5000, cancellation.Token);
                if (cancellation.IsCancellationRequested)
                {
                    return;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!bridgeService.IsRunning
                        || bridgeService.HasKeyboardSubscriber
                        || bridgeService.HasMouseSubscriber
                        || hasShownBridgeConnectedToast
                        || hasShownBridgeConnectionFailureToast)
                    {
                        return;
                    }

                    hasShownBridgeConnectionFailureToast = true;
                    ShowBridgeStatusToast("iPad", "iPad", false, "연결 안됨");
                    AddActivity("브릿지", "iPad가 아직 Bluetooth HID에 연결되지 않았습니다.");
                });
            }
            catch (OperationCanceledException)
            {
            }
        });
    }

    private void CancelBridgeConnectionFeedback()
    {
        bridgeConnectionToastCancellation?.Cancel();
        bridgeConnectionToastCancellation?.Dispose();
        bridgeConnectionToastCancellation = null;
    }

    private async Task InitializeScreenshotShareAsync()
    {
        try
        {
            await screenshotShareService.StartAsync();
            AddActivity("Screenshot", $"Ready: {screenshotShareService.LocalUrl}");
        }
        catch (Exception ex)
        {
            AddActivity("Screenshot", $"Server failed: {ex.Message}");
        }
    }

    private async Task CaptureAndShareScreenshotAsync()
    {
        try
        {
            var result = await screenshotShareService.CaptureLatestAsync();
            System.Windows.Clipboard.SetText(result.Url);
            AddActivity("Screenshot", $"Captured. URL copied: {result.Url}");
        }
        catch (Exception ex)
        {
            AddActivity("Screenshot", $"Capture failed: {ex.Message}");
        }
    }

    private async Task ShareClipboardImageAsync()
    {
        try
        {
            if (!System.Windows.Clipboard.ContainsImage())
            {
                AddActivity("Screenshot", "Clipboard has no image. Use Win+Shift+S first.");
                return;
            }

            var image = System.Windows.Clipboard.GetImage();
            if (image is null)
            {
                AddActivity("Screenshot", "Clipboard image could not be read.");
                return;
            }

            var result = await screenshotShareService.SaveClipboardImageAsync(image);
            System.Windows.Clipboard.SetText(result.Url);
            AddActivity("Screenshot", $"Clipboard image shared. URL copied: {result.Url}");
        }
        catch (Exception ex)
        {
            AddActivity("Screenshot", $"Clipboard image share failed: {ex.Message}");
        }
    }

    private async Task TypeClipboardTextAsync(bool switchInputSourceFirst = false)
    {
        if (isClipboardTypingInProgress)
        {
            AddActivity("Clipboard", "Clipboard typing is already running; ignored duplicate request.");
            return;
        }

        isClipboardTypingInProgress = true;
        AddActivity("Clipboard", $"Typing requested. bridge={bridgeService.IsRunning}, keyboard={bridgeService.HasKeyboardSubscriber}, mouse={bridgeService.HasMouseSubscriber}, switchInputSourceFirst={switchInputSourceFirst}");
        if (!bridgeService.IsRunning)
        {
            AddActivity("클립보드", "먼저 브릿지를 시작하고 iPad의 텍스트 입력칸을 선택한 뒤 클립보드를 입력하세요.");
            isClipboardTypingInProgress = false;
            return;
        }

        if (!System.Windows.Clipboard.ContainsText())
        {
            AddActivity("클립보드", "PC 클립보드에 텍스트가 없습니다.");
            isClipboardTypingInProgress = false;
            return;
        }

        var originalText = System.Windows.Clipboard.GetText();
        AddActivity("Clipboard", $"Clipboard text length={originalText.Length}, preview=\"{CreateClipboardPreview(originalText)}\"");
        if (string.IsNullOrEmpty(originalText))
        {
            AddActivity("클립보드", "PC 클립보드 텍스트가 비어 있습니다.");
            isClipboardTypingInProgress = false;
            return;
        }

        var text = NormalizeClipboardTextForHidTypingSafe(originalText);
        if (switchInputSourceFirst)
        {
            text = PreventGoodNotesAutoNumberedLists(text);
        }

        if (!string.Equals(originalText, text, StringComparison.Ordinal))
        {
            AddActivity("Clipboard", $"Normalized text for HID typing. preview=\"{CreateClipboardPreview(text)}\"");
        }

        if (!bridgeService.HasKeyboardSubscriber)
        {
            AddActivity("Clipboard", "Warning: iPad has not subscribed to keyboard input yet. The app will still try both HID keyboard paths.");
        }

        var smartTokens = new List<ClipboardTypingToken>();
        if (switchInputSourceFirst)
        {
            if (!TryCreateSmartClipboardTypingTokens(text, out smartTokens, out var smartUnsupportedCharacter))
            {
                AddActivity("Clipboard", $"Smart typing unsupported character: '{DescribeClipboardCharacter(smartUnsupportedCharacter)}'.");
                isClipboardTypingInProgress = false;
                return;
            }
        }
        else if (TryFindUnsupportedClipboardCharacter(text, out var unsupportedCharacter))
        {
            AddActivity("Clipboard", $"Unsupported character: '{DescribeClipboardCharacter(unsupportedCharacter)}'. Korean/emoji Unicode text cannot be typed by the current HID key-map.");
            AddActivity("클립보드", $"중단: 현재 클립보드 입력은 영문/숫자/기본 기호만 안정적으로 지원합니다. 지원하지 않는 문자: '{DescribeClipboardCharacter(unsupportedCharacter)}'");
            isClipboardTypingInProgress = false;
            return;
        }

        try
        {
            using var cancellation = new CancellationTokenSource();
            clipboardTypingCancellation = cancellation;

            if (switchInputSourceFirst)
            {
                await Task.Delay(ClipboardTypingStartDelayMs, cancellation.Token);
            }

            pressedKeys.Clear();
            await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());

            if (switchInputSourceFirst)
            {
                var smartTypedCount = await TypeSmartClipboardTokensAsync(smartTokens, cancellation.Token);
                AddActivity("Clipboard", $"Smart typing finished. typed={smartTypedCount}, tokens={smartTokens.Count}");
                return;
            }

            var typedCount = 0;
            var skippedCount = 0;

            foreach (var character in text)
            {
                cancellation.Token.ThrowIfCancellationRequested();

                if (character == '\r')
                {
                    continue;
                }

                if (!HidKeyboardReport.TryCreateTextInputReport(character, out var report))
                {
                    skippedCount++;
                    continue;
                }

                await bridgeService.SendKeyboardReportAsync(activeDevice, report, $"clipboard '{DescribeClipboardCharacter(character)}'", ClipboardCharacterHoldMs, ClipboardCharacterReleaseMs);
                typedCount++;
            }

            AddActivity("클립보드", $"iPad로 클립보드 문자 {typedCount}개를 입력했습니다. 지원하지 않아 건너뜀: {skippedCount}개.");
            AddActivity("Clipboard", $"Typing finished. typed={typedCount}, skipped={skippedCount}");
        }
        catch (OperationCanceledException)
        {
            pressedKeys.Clear();
            await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());
            AddActivity("Clipboard", "Clipboard typing canceled by Esc.");
        }
        finally
        {
            clipboardTypingCancellation = null;
            isClipboardTypingInProgress = false;
        }
    }

    private async Task SendIpadInputSourceToggleAsync(CancellationToken cancellationToken = default)
    {
        AddActivity("Clipboard", "Sending Ctrl+Space before clipboard typing to switch the iPad hardware keyboard input source.");
        await bridgeService.SendKeyboardReportAsync(activeDevice, new byte[] { 0x01, 0x00, 0x2C, 0x00, 0x00, 0x00, 0x00, 0x00 }, "clipboard input source toggle Ctrl+Space", InputSourceToggleHoldMs, 0);
        await bridgeService.SendKeyboardStateAsync(activeDevice, Array.Empty<CapturedKey>());
        await Task.Delay(InputSourceToggleSettleMs, cancellationToken);
    }

    private async Task<int> TypeSmartClipboardTokensAsync(IReadOnlyList<ClipboardTypingToken> tokens, CancellationToken cancellationToken)
    {
        var initialKoreanInputSource = true;
        var isKoreanInputSource = initialKoreanInputSource;
        var typedCount = 0;

        AddActivity("Clipboard", "Smart typing assumes initial iPad input source is Korean.");

        try
        {
            foreach (var token in tokens)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (token.UsesKoreanInputSource != isKoreanInputSource)
                {
                    await SendIpadInputSourceToggleAsync(cancellationToken);
                    isKoreanInputSource = token.UsesKoreanInputSource;
                }

                foreach (var character in token.Text)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (character == '\r')
                    {
                        continue;
                    }

                    if (!HidKeyboardReport.TryCreateTextInputReport(character, out var report))
                    {
                        continue;
                    }

                    await bridgeService.SendKeyboardReportAsync(activeDevice, report, $"smart clipboard '{DescribeClipboardCharacter(character)}'", ClipboardCharacterHoldMs, ClipboardCharacterReleaseMs);
                    typedCount++;
                }
            }

            return typedCount;
        }
        finally
        {
            if (isKoreanInputSource != initialKoreanInputSource)
            {
                await SendIpadInputSourceToggleAsync();
            }
        }
    }

    private static bool TryCreateSmartClipboardTypingTokens(string text, out List<ClipboardTypingToken> tokens, out char unsupportedCharacter)
    {
        var resultTokens = new List<ClipboardTypingToken>();
        tokens = resultTokens;
        unsupportedCharacter = '\0';

        var builder = new StringBuilder();
        bool? currentUsesKoreanInputSource = null;

        void Flush()
        {
            if (builder.Length == 0 || currentUsesKoreanInputSource is null)
            {
                return;
            }

            resultTokens.Add(new ClipboardTypingToken(currentUsesKoreanInputSource.Value, builder.ToString()));
            builder.Clear();
        }

        void Append(bool usesKoreanInputSource, string value)
        {
            if (currentUsesKoreanInputSource != usesKoreanInputSource)
            {
                Flush();
                currentUsesKoreanInputSource = usesKoreanInputSource;
            }

            builder.Append(value);
        }

        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }

            if (TryConvertHangulToDubeolsikKeys(character, out var koreanKeys))
            {
                Append(usesKoreanInputSource: true, koreanKeys);
                continue;
            }

            if (IsNeutralSmartTypingCharacter(character))
            {
                Append(currentUsesKoreanInputSource ?? true, character.ToString());
                continue;
            }

            if (HidKeyboardReport.TryCreateTextInputReport(character, out _))
            {
                Append(usesKoreanInputSource: false, character.ToString());
                continue;
            }

            unsupportedCharacter = character;
            return false;
        }

        Flush();
        return true;
    }

    private static bool IsNeutralSmartTypingCharacter(char character)
    {
        return char.IsWhiteSpace(character)
            || char.IsDigit(character)
            || char.IsPunctuation(character)
            || char.IsSymbol(character);
    }

    private static bool TryConvertHangulToDubeolsikKeys(char character, out string keys)
    {
        string[] initials =
        {
            "r", "R", "s", "e", "E", "f", "a", "q", "Q", "t", "T", "d", "w", "W", "c", "z", "x", "v", "g"
        };

        string[] vowels =
        {
            "k", "o", "i", "O", "j", "p", "u", "P", "h", "hk", "ho", "hl", "y", "n", "nj", "np", "nl", "b", "m", "ml", "l"
        };

        string[] finals =
        {
            "", "r", "R", "rt", "s", "sw", "sg", "e", "f", "fr", "fa", "fq", "ft", "fx", "fv", "fg", "a", "q", "qt", "t", "T", "d", "w", "c", "z", "x", "v", "g"
        };

        var code = character;
        if (code is >= '\uAC00' and <= '\uD7A3')
        {
            var syllableIndex = code - '\uAC00';
            var initialIndex = syllableIndex / (21 * 28);
            var vowelIndex = syllableIndex % (21 * 28) / 28;
            var finalIndex = syllableIndex % 28;
            keys = initials[initialIndex] + vowels[vowelIndex] + finals[finalIndex];
            return true;
        }

        keys = character switch
        {
            '\u3131' => "r",
            '\u3132' => "R",
            '\u3134' => "s",
            '\u3137' => "e",
            '\u3138' => "E",
            '\u3139' => "f",
            '\u3141' => "a",
            '\u3142' => "q",
            '\u3143' => "Q",
            '\u3145' => "t",
            '\u3146' => "T",
            '\u3147' => "d",
            '\u3148' => "w",
            '\u3149' => "W",
            '\u314A' => "c",
            '\u314B' => "z",
            '\u314C' => "x",
            '\u314D' => "v",
            '\u314E' => "g",
            '\u314F' => "k",
            '\u3150' => "o",
            '\u3151' => "i",
            '\u3152' => "O",
            '\u3153' => "j",
            '\u3154' => "p",
            '\u3155' => "u",
            '\u3156' => "P",
            '\u3157' => "h",
            '\u3158' => "hk",
            '\u3159' => "ho",
            '\u315A' => "hl",
            '\u315B' => "y",
            '\u315C' => "n",
            '\u315D' => "nj",
            '\u315E' => "np",
            '\u315F' => "nl",
            '\u3160' => "b",
            '\u3161' => "m",
            '\u3162' => "ml",
            '\u3163' => "l",
            _ => string.Empty
        };

        return keys.Length > 0;
    }

    private static string CreateClipboardPreview(string text)
    {
        var normalized = text.Replace("\r", "\\r").Replace("\n", "\\n").Replace("\t", "\\t");
        return normalized.Length <= 32 ? normalized : normalized[..32] + "...";
    }

    private static string NormalizeClipboardTextForHidTypingSafe(string text)
    {
        return text
            .Replace("\u00A0", " ")
            .Replace("\u2000", " ")
            .Replace("\u2001", " ")
            .Replace("\u2002", " ")
            .Replace("\u2003", " ")
            .Replace("\u2004", " ")
            .Replace("\u2005", " ")
            .Replace("\u2006", " ")
            .Replace("\u2007", " ")
            .Replace("\u2008", " ")
            .Replace("\u2009", " ")
            .Replace("\u200A", " ")
            .Replace("\u202F", " ")
            .Replace("\u205F", " ")
            .Replace("\u3000", " ")
            .Replace("\u200B", string.Empty)
            .Replace("\u200C", string.Empty)
            .Replace("\u200D", string.Empty)
            .Replace("\uFEFF", string.Empty)
            .Replace("\u2192", "->")
            .Replace("\u2190", "<-")
            .Replace("\u21D2", "=>")
            .Replace("\u21D0", "<=")
            .Replace("\u2194", "<->")
            .Replace("\u2264", "<=")
            .Replace("\u2265", ">=")
            .Replace("\u2260", "!=")
            .Replace("\u2248", "~=")
            .Replace("\u00B1", "+/-")
            .Replace("\u2212", "-")
            .Replace("\u00D7", "x")
            .Replace("\u00F7", "/")
            .Replace("\u2217", "*")
            .Replace("\u221A", "sqrt")
            .Replace("\u221E", "infinity")
            .Replace("\u222B", "integral")
            .Replace("\u2206", "delta")
            .Replace("\u2202", "partial")
            .Replace("\u2211", "sum")
            .Replace("\u2208", "in")
            .Replace("\u2209", "not in")
            .Replace("\u2282", "subset")
            .Replace("\u2283", "superset")
            .Replace("\u2229", "intersect")
            .Replace("\u222A", "union")
            .Replace("\u03B1", "alpha")
            .Replace("\u03B2", "beta")
            .Replace("\u03B3", "gamma")
            .Replace("\u03B4", "delta")
            .Replace("\u03B5", "epsilon")
            .Replace("\u03B8", "theta")
            .Replace("\u03BB", "lambda")
            .Replace("\u03BC", "mu")
            .Replace("\u03C0", "pi")
            .Replace("\u03C1", "rho")
            .Replace("\u03C3", "sigma")
            .Replace("\u03C4", "tau")
            .Replace("\u03C6", "phi")
            .Replace("\u03C7", "chi")
            .Replace("\u03C9", "omega")
            .Replace("\u0391", "Alpha")
            .Replace("\u0392", "Beta")
            .Replace("\u0393", "Gamma")
            .Replace("\u0394", "Delta")
            .Replace("\u0398", "Theta")
            .Replace("\u039B", "Lambda")
            .Replace("\u03A0", "Pi")
            .Replace("\u03A3", "Sigma")
            .Replace("\u03A6", "Phi")
            .Replace("\u03A9", "Omega")
            .Replace("\u00B2", "^2")
            .Replace("\u00B3", "^3")
            .Replace("\u00B9", "^1")
            .Replace("\u2070", "^0")
            .Replace("\u2074", "^4")
            .Replace("\u2075", "^5")
            .Replace("\u2076", "^6")
            .Replace("\u2077", "^7")
            .Replace("\u2078", "^8")
            .Replace("\u2079", "^9")
            .Replace("\u207A", "^+")
            .Replace("\u207B", "^-")
            .Replace("\u2080", "_0")
            .Replace("\u2081", "_1")
            .Replace("\u2082", "_2")
            .Replace("\u2083", "_3")
            .Replace("\u2084", "_4")
            .Replace("\u2085", "_5")
            .Replace("\u2086", "_6")
            .Replace("\u2087", "_7")
            .Replace("\u2088", "_8")
            .Replace("\u2089", "_9")
            .Replace("\u00BC", "1/4")
            .Replace("\u00BD", "1/2")
            .Replace("\u00BE", "3/4")
            .Replace("\u201C", "\"")
            .Replace("\u201D", "\"")
            .Replace("\u2018", "'")
            .Replace("\u2019", "'")
            .Replace("\u2013", "-")
            .Replace("\u2014", "-")
            .Replace("\u2026", "...")
            .Replace("\u00B7", "*")
            .Replace("\u2022", "*")
            .Replace("\u25CF", "*")
            .Replace("\u25CB", "o")
            .Replace("\u25A0", "*")
            .Replace("\u25A1", "[]")
            .Replace("\u2713", "check")
            .Replace("\u2714", "check")
            .Replace("\u2717", "x")
            .Replace("\u2718", "x");
    }

    private static string NormalizeClipboardTextForHidTyping(string text)
    {
        return text
            .Replace("→", "->")
            .Replace("←", "<-")
            .Replace("⇒", "=>")
            .Replace("⇐", "<=")
            .Replace("↔", "<->")
            .Replace("“", "\"")
            .Replace("”", "\"")
            .Replace("‘", "'")
            .Replace("’", "'")
            .Replace("–", "-")
            .Replace("—", "-")
            .Replace("…", "...")
            .Replace("·", "*")
            .Replace("•", "*");
    }

    private static string PreventGoodNotesAutoNumberedLists(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            lines[index] = ReplaceLeadingNumberedListMarker(lines[index]);
        }

        return string.Join('\n', lines);
    }

    private static string ReplaceLeadingNumberedListMarker(string line)
    {
        var index = 0;
        while (index < line.Length && line[index] == ' ')
        {
            index++;
        }

        var digitStart = index;
        while (index < line.Length && char.IsDigit(line[index]))
        {
            index++;
        }

        if (index == digitStart || index >= line.Length || line[index] != '.')
        {
            return line;
        }

        var nextIndex = index + 1;
        if (nextIndex < line.Length && !char.IsWhiteSpace(line[nextIndex]))
        {
            return line;
        }

        return line[..index] + ")" + line[(index + 1)..];
    }

    private static bool TryFindUnsupportedClipboardCharacter(string text, out char unsupportedCharacter)
    {
        foreach (var character in text)
        {
            if (character == '\r')
            {
                continue;
            }

            if (!HidKeyboardReport.TryCreateTextInputReport(character, out _))
            {
                unsupportedCharacter = character;
                return true;
            }
        }

        unsupportedCharacter = '\0';
        return false;
    }

    private static string DescribeClipboardCharacter(char character)
    {
        return character switch
        {
            '\n' => "\\n",
            '\t' => "\\t",
            ' ' => "space",
            _ => character.ToString()
        };
    }

    private void ResetMouseState()
    {
        lock (mouseStateLock)
        {
            mouseButtons = 0;
            pendingMouseDeltaX = 0;
            pendingMouseDeltaY = 0;
            mouseSendLoopRunning = false;
            lastPointerEvent = DateTime.MinValue;
            lastPointerLogEvent = DateTime.MinValue;
        }
    }

    private void StopMouseCaptureImmediately()
    {
        CancelMouseSendLoop();
        inputHookService.SuppressForwardedPointerEvents = false;
        inputHookService.CapturePointerEvents = false;
        ResetMouseState();
        ReleaseLocalMouseButtons();
    }

    private void CancelMouseSendLoop()
    {
        mouseSendLoopCancellation?.Cancel();
        mouseSendLoopCancellation?.Dispose();
        mouseSendLoopCancellation = null;
    }

    private void QueueMouseReport(sbyte deltaX, sbyte deltaY, byte buttons, bool shouldLog)
    {
        bool shouldStartLoop;
        CancellationToken token;

        lock (mouseStateLock)
        {
            pendingMouseDeltaX += deltaX;
            pendingMouseDeltaY += deltaY;
            mouseButtons = buttons;
            shouldStartLoop = !mouseSendLoopRunning;
            if (shouldStartLoop)
            {
                CancelMouseSendLoop();
                mouseSendLoopCancellation = new CancellationTokenSource();
                mouseSendLoopRunning = true;
            }

            token = mouseSendLoopCancellation?.Token ?? CancellationToken.None;
        }

        if (shouldLog)
        {
            _ = Dispatcher.InvokeAsync(() => AddActivity("마우스", $"dx={deltaX}, dy={deltaY}, buttons={buttons}"));
        }

        if (shouldStartLoop)
        {
            _ = SendQueuedMouseReportsAsync(token);
        }
    }

    private async Task SendQueuedMouseReportsAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                sbyte deltaX;
                sbyte deltaY;
                byte buttons;

                lock (mouseStateLock)
                {
                    if (pendingMouseDeltaX == 0 && pendingMouseDeltaY == 0)
                    {
                        mouseSendLoopRunning = false;
                        return;
                    }

                    deltaX = TakeMouseDelta(ref pendingMouseDeltaX);
                    deltaY = TakeMouseDelta(ref pendingMouseDeltaY);
                    buttons = mouseButtons;
                }

                await bridgeService.SendMouseReportAsync(activeDevice, deltaX, deltaY, buttons, 0, 0);
                await Task.Delay(MouseSendIntervalMs, cancellationToken);

                if (!bridgeService.IsRunning || !isMouseSignalEnabled)
                {
                    lock (mouseStateLock)
                    {
                        pendingMouseDeltaX = 0;
                        pendingMouseDeltaY = 0;
                        mouseSendLoopRunning = false;
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            lock (mouseStateLock)
            {
                pendingMouseDeltaX = 0;
                pendingMouseDeltaY = 0;
                mouseButtons = 0;
                mouseSendLoopRunning = false;
            }
        }
        catch (Exception ex)
        {
            lock (mouseStateLock)
            {
                mouseSendLoopRunning = false;
            }

            await Dispatcher.InvokeAsync(() => AddActivity("마우스", $"마우스 전송 실패: {ex.Message}"));
        }
    }

    private DateTime lastModifierSyncTime = DateTime.MinValue;

    private async Task SendMouseButtonReportAsync(sbyte deltaX, sbyte deltaY, byte buttons, sbyte wheel, bool shouldLog)
    {
        try
        {
            sbyte hWheel = 0;
            var shouldConfirmButtonRelease = buttons == 0;
            if (shouldConfirmButtonRelease)
            {
                CancelMouseSendLoop();
                lock (mouseStateLock)
                {
                    pendingMouseDeltaX = 0;
                    pendingMouseDeltaY = 0;
                    mouseButtons = 0;
                    mouseSendLoopRunning = false;
                }
            }
            if (wheel != 0)
            {
                var now = DateTime.Now;
                bool shouldSyncModifier = false;
                
                lock (mouseStateLock)
                {
                    lastWheelEvent = now;
                    isModifierStickyActive = true;
                    
                    // Only sync every 100ms during continuous scrolling to prevent BLE congestion
                    if ((now - lastModifierSyncTime).TotalMilliseconds > 100)
                    {
                        shouldSyncModifier = true;
                        lastModifierSyncTime = now;
                    }
                }

                if (shouldSyncModifier && (pressedKeys.ContainsKey(VkControl) || pressedKeys.ContainsKey(VkLControl) || pressedKeys.ContainsKey(VkRControl) || 
                                           pressedKeys.ContainsKey(VkShift) || pressedKeys.ContainsKey(VkLShift) || pressedKeys.ContainsKey(VkRShift)))
                {
                    await bridgeService.SendKeyboardStateAsync(activeDevice, pressedKeys.Values.ToList());
                    await Task.Delay(15); // Increased gap for iPad to process modifier before wheel
                }
                
                // Horizontal Scroll: If Shift is held, move vertical wheel to horizontal wheel
                if (pressedKeys.ContainsKey(VkShift) || pressedKeys.ContainsKey(VkLShift) || pressedKeys.ContainsKey(VkRShift))
                {
                    hWheel = wheel;
                    wheel = 0;
                }
                // Zoom Booster: Triple the wheel delta if Ctrl is held to ensure iPad registers the gesture
                else if (pressedKeys.ContainsKey(VkControl) || pressedKeys.ContainsKey(VkLControl) || pressedKeys.ContainsKey(VkRControl))
                {
                    wheel = (sbyte)Math.Clamp(wheel * 3, sbyte.MinValue, sbyte.MaxValue);
                }
            }

            await bridgeService.SendMouseReportAsync(activeDevice, deltaX, deltaY, buttons, wheel, hWheel);
            if (shouldConfirmButtonRelease)
            {
                await Task.Delay(12);
                await bridgeService.SendMouseReportAsync(activeDevice, 0, 0, 0, 0, 0);
            }

            if (shouldLog)
            {
                await Dispatcher.InvokeAsync(() => AddActivity("마우스", $"dx={deltaX}, dy={deltaY}, wheel={wheel}, buttons={buttons}"));
            }
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => AddActivity("마우스", $"마우스 버튼 전송 실패: {ex.Message}"));
        }
    }

    private async Task SendMouseKeyboardShortcutAsync(byte[] report, string description)
    {
        try
        {
            await bridgeService.SendKeyboardReportAsync(activeDevice, report, description);
            await Dispatcher.InvokeAsync(() => AddActivity("마우스", $"마우스 버튼 -> {description}"));
        }
        catch (Exception ex)
        {
            await Dispatcher.InvokeAsync(() => AddActivity("마우스", $"마우스 버튼 전송 실패: {ex.Message}"));
        }
    }

    private static sbyte TakeMouseDelta(ref int pendingDelta)
    {
        var nextDelta = Math.Clamp(pendingDelta, sbyte.MinValue, sbyte.MaxValue);
        pendingDelta -= nextDelta;
        return (sbyte)nextDelta;
    }

    private async Task SendMouseTestPatternAsync()
    {
        await bridgeService.SendMouseReportAsync(activeDevice, 0, 0, 0, 0, 0);

        for (var index = 0; index < 8; index++)
        {
            await bridgeService.SendMouseReportAsync(activeDevice, 24, 0, 0, 0, 0);
            await Task.Delay(16);
        }

        for (var index = 0; index < 5; index++)
        {
            await bridgeService.SendMouseReportAsync(activeDevice, 0, 18, 0, 0, 0);
            await Task.Delay(16);
        }

        for (var index = 0; index < 8; index++)
        {
            await bridgeService.SendMouseReportAsync(activeDevice, -24, 0, 0, 0, 0);
            await Task.Delay(16);
        }

        for (var index = 0; index < 5; index++)
        {
            await bridgeService.SendMouseReportAsync(activeDevice, 0, -18, 0, 0, 0);
            await Task.Delay(16);
        }

        await bridgeService.SendMouseReportAsync(activeDevice, 0, 0, 0, 0, 0);
    }

    private static void ReleaseLocalModifierKeys()
    {
        foreach (var virtualKey in new[] { VkControl, VkLControl, VkRControl, VkMenu, VkLMenu, VkRMenu, VkLWin, VkRWin })
        {
            keybd_event(virtualKey, 0, KeyEventFKeyUp, UIntPtr.Zero);
        }
    }

    private static void ReleaseLocalMouseButtons()
    {
        uint flags = 0;

        if ((GetAsyncKeyState(VkLButton) & 0x8000) != 0)
        {
            flags |= MouseEventFLeftUp;
        }

        if ((GetAsyncKeyState(VkRButton) & 0x8000) != 0)
        {
            flags |= MouseEventFRightUp;
        }

        if ((GetAsyncKeyState(VkMButton) & 0x8000) != 0)
        {
            flags |= MouseEventFMiddleUp;
        }

        if (flags != 0)
        {
            mouse_event(flags, 0, 0, 0, UIntPtr.Zero);
        }
    }

    private void UpdatePointerCapture()
    {
        inputHookService.CapturePointerEvents = bridgeService.IsRunning && isMouseSignalEnabled;
    }

    private void UpdateMouseButtons(ushort buttonFlags)
    {
        if ((buttonFlags & RawMouseLeftButtonDown) != 0)
        {
            mouseButtons |= 0x01;
        }

        if ((buttonFlags & RawMouseLeftButtonUp) != 0)
        {
            mouseButtons &= 0xFE;
        }

        if ((buttonFlags & RawMouseRightButtonDown) != 0)
        {
            mouseButtons |= 0x02;
        }

        if ((buttonFlags & RawMouseRightButtonUp) != 0)
        {
            mouseButtons &= 0xFD;
        }

        if ((buttonFlags & RawMouseMiddleButtonDown) != 0)
        {
            mouseButtons |= 0x04;
        }

        if ((buttonFlags & RawMouseMiddleButtonUp) != 0)
        {
            mouseButtons &= 0xFB;
        }
    }

    private static sbyte ScaleMouseDelta(int value, double scale)
    {
        var scaledValue = (int)Math.Round(value * scale);
        return (sbyte)Math.Clamp(scaledValue, sbyte.MinValue, sbyte.MaxValue);
    }

    private static bool TryGetConsumerControlUsage(GlobalKeyEventArgs e, out ushort usage)
    {
        usage = e.Key switch
        {
            System.Windows.Input.Key.Scroll => ConsumerVolumeDecrement,
            System.Windows.Input.Key.Pause => ConsumerVolumeIncrement,
            System.Windows.Input.Key.PrintScreen => ConsumerMute,
            _ => 0
        };

        return usage != 0;
    }

    private static sbyte ScaleMouseWheel(ushort buttonFlags, ushort buttonData)
    {
        if ((buttonFlags & RawMouseWheel) == 0)
        {
            return 0;
        }

        var signedButtonData = unchecked((short)buttonData);
        var wheelSteps = -(signedButtonData / MouseWheelDelta);
        return (sbyte)Math.Clamp(wheelSteps, sbyte.MinValue, sbyte.MaxValue);
    }

    private static bool TryGetMouseNavigationShortcut(ushort buttonFlags, out byte[] report, out string description)
    {
        const byte leftGui = 0x08;
        const byte openBracket = 0x2F;
        const byte closeBracket = 0x30;

        if ((buttonFlags & RawMouseButton4Down) != 0)
        {
            report = new byte[] { leftGui, 0x00, openBracket, 0x00, 0x00, 0x00, 0x00, 0x00 };
            description = "Command+[ Back";
            return true;
        }

        if ((buttonFlags & RawMouseButton5Down) != 0)
        {
            report = new byte[] { leftGui, 0x00, closeBracket, 0x00, 0x00, 0x00, 0x00, 0x00 };
            description = "Command+] Forward";
            return true;
        }

        report = Array.Empty<byte>();
        description = string.Empty;
        return false;
    }

    private void LoadWindowIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "keybridge.ico");
        if (File.Exists(iconPath))
        {
            var iconUri = new System.Uri(iconPath, System.UriKind.Absolute);
            Icon = BitmapFrame.Create(iconUri);
        }
    }

    private void AddActivity(string channel, string message)
    {
        ActivityEvents.Insert(0, new ActivityEvent(DateTime.Now, channel, message));

        while (ActivityEvents.Count > 80)
        {
            ActivityEvents.RemoveAt(ActivityEvents.Count - 1);
        }
    }

    private void RefreshStatus()
    {
        ActiveDeviceNameText.Text = "노트북 -> iPad";
        StatusText.Text = bridgeService.IsRunning
            ? "연결됨"
            : "연결 준비 완료";
        BridgeToggleButton.Content = bridgeService.IsRunning ? "해제" : "연결";
        KeyboardStateText.Text = bridgeService.IsRunning ? "연결됨" : "준비됨";
        MouseStateText.Text = bridgeService.IsRunning
            ? isMouseSignalEnabled ? "연결됨" : "꺼짐"
            : "준비됨";
        MouseSignalSummaryText.Text = isMouseSignalEnabled ? "켜짐" : "꺼짐";
        RemoteDeviceText.Text = bridgeService.IsRunning
            ? "페어링 중입니다. iPad에서 '액세서리'를 찾으세요. 연결 후 이름이 바뀔 수 있습니다."
                            : "연결 준비 완료. Alt+Ctrl+Q 또는 연결 버튼을 누르세요.";
    }

    private void RegisterClipboardHotKeys(IntPtr windowHandle)
    {
        if (!RegisterHotKey(windowHandle, HotKeyClipboardText, ModNoRepeat, VkF3))
        {
            AddActivity("Hotkey", "F3 등록 실패. Fn+F3 또는 클립보드 입력 버튼을 사용하세요.");
        }

        if (!RegisterHotKey(windowHandle, HotKeyClipboardImage, ModNoRepeat, VkF4))
        {
            AddActivity("Hotkey", "F4 등록 실패. Share Image 버튼을 사용하세요.");
        }

        if (!RegisterHotKey(windowHandle, HotKeyClipboardImageFallback, ModNoRepeat | ModControl | ModAlt, VkI))
        {
            AddActivity("Hotkey", "Ctrl+Alt+I 등록 실패. Share Image 버튼을 사용하세요.");
        }
    }

    private void UnregisterClipboardHotKeys()
    {
        var windowHandle = new WindowInteropHelper(this).Handle;
        if (windowHandle == IntPtr.Zero)
        {
            return;
        }

        UnregisterHotKey(windowHandle, HotKeyClipboardText);
        UnregisterHotKey(windowHandle, HotKeyClipboardImage);
        UnregisterHotKey(windowHandle, HotKeyClipboardImageFallback);
    }

    private void ShowBridgeStatusToast(string label, string symbol, bool isConnected, string? statusText = null)
    {
        bridgeStatusToast?.Close();
        bridgeStatusToast = new BridgeStatusToastWindow(label, symbol, isConnected, statusText);
        bridgeStatusToast.Closed += (_, _) => bridgeStatusToast = null;
        bridgeStatusToast.ShowBriefly();
    }

    private void InitializeTrayIcon()
    {
        var contextMenu = new Forms.ContextMenuStrip();
        contextMenu.Items.Add("KeyBridge 열기", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        contextMenu.Items.Add("종료", null, (_, _) => Dispatcher.Invoke(ExitFromTray));

        trayIcon = new Forms.NotifyIcon
        {
            Text = "KeyBridge",
            Icon = LoadTrayIcon(),
            ContextMenuStrip = contextMenu,
            Visible = true
        };

        trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private Drawing.Icon LoadTrayIcon()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "keybridge.ico");
        if (File.Exists(iconPath))
        {
            return new Drawing.Icon(iconPath);
        }

        return Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty)
            ?? Drawing.SystemIcons.Application;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            HideToTray();
        }
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (isExitRequested)
        {
            return;
        }

        e.Cancel = true;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();

        if (hasShownTrayTip || trayIcon is null)
        {
            return;
        }

        trayIcon.ShowBalloonTip(2500, "KeyBridge", "KeyBridge가 트레이에서 계속 실행 중입니다.", Forms.ToolTipIcon.Info);
        hasShownTrayTip = true;
    }

    private void ShowFromTray()
    {
        Show();
        WindowState = WindowState.Normal;
        ShowInTaskbar = true;
        Activate();
    }

    private void ExitFromTray()
    {
        isExitRequested = true;
        Close();
    }

    private static bool IsStartWithWindowsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
        return key?.GetValue(RunRegistryName) is string value
            && string.Equals(value, GetStartWithWindowsCommand(), StringComparison.OrdinalIgnoreCase);
    }

    private static void SetStartWithWindows(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);

        if (enabled)
        {
            key.SetValue(RunRegistryName, GetStartWithWindowsCommand(), RegistryValueKind.String);
            return;
        }

        key.DeleteValue(RunRegistryName, throwOnMissingValue: false);
    }

    private static string GetStartWithWindowsCommand()
    {
        var executablePath = Environment.ProcessPath ?? System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new InvalidOperationException("Unable to determine KeyBridge executable path.");
        }

        return $"\"{executablePath}\"";
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        UnregisterClipboardHotKeys();
        trayIcon?.Dispose();
        trayIcon = null;

        if (bridgeService.IsRunning)
        {
            await StopBridgeAsync("앱 종료로 브릿지를 중지했습니다.");
        }

        screenshotShareService.Dispose();
        inputHookService.Dispose();
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    private static void RegisterRawMouseInput(IntPtr targetWindow)
    {
        var device = new RawInputDevice
        {
            UsagePage = 0x01,
            Usage = 0x02,
            Flags = RidevInputSink,
            Target = targetWindow
        };

        if (!RegisterRawInputDevices([device], 1, Marshal.SizeOf<RawInputDevice>()))
        {
            throw new InvalidOperationException("Unable to register raw mouse input.");
        }
    }

    private static bool TryReadRawMouseInput(IntPtr rawInputHandle, out RawMouseInput mouseInput)
    {
        mouseInput = default;
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RawInputHeader>();

        GetRawInputData(rawInputHandle, RidInput, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
        {
            return false;
        }

        var buffer = new byte[size];
        var handle = GCHandle.Alloc(buffer, GCHandleType.Pinned);

        try
        {
            var result = GetRawInputData(rawInputHandle, RidInput, handle.AddrOfPinnedObject(), ref size, headerSize);
            if (result == uint.MaxValue)
            {
                return false;
            }

            var header = Marshal.PtrToStructure<RawInputHeader>(handle.AddrOfPinnedObject());
            if (header.Type != RawInputTypeMouse)
            {
                return false;
            }

            var offset = Marshal.SizeOf<RawInputHeader>();
            var buttonFlags = BitConverter.ToUInt16(buffer, offset + 4);
            var buttonData = BitConverter.ToUInt16(buffer, offset + 6);
            var deltaX = BitConverter.ToInt32(buffer, offset + 12);
            var deltaY = BitConverter.ToInt32(buffer, offset + 16);
            mouseInput = new RawMouseInput(deltaX, deltaY, buttonFlags, buttonData);
            return true;
        }
        finally
        {
            handle.Free();
        }
    }

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, int vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(
        [In] RawInputDevice[] rawInputDevices,
        uint numberDevices,
        int size);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetRawInputData(
        IntPtr rawInput,
        uint command,
        IntPtr data,
        ref uint size,
        uint sizeHeader);

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputDevice
    {
        public ushort UsagePage;
        public ushort Usage;
        public int Flags;
        public IntPtr Target;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RawInputHeader
    {
        public int Type;
        public int Size;
        public IntPtr Device;
        public IntPtr WParam;
    }

    private readonly record struct RawMouseInput(int DeltaX, int DeltaY, ushort ButtonFlags, ushort ButtonData);
}
