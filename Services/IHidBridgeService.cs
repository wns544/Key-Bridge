using KeyboardPadBridge.Models;
using System.Windows.Input;

namespace KeyboardPadBridge.Services;

public interface IHidBridgeService
{
    event EventHandler<string>? DiagnosticMessage;
    event EventHandler<bool>? MouseSubscriberChanged;

    bool IsRunning { get; }

    bool HasKeyboardSubscriber { get; }

    bool HasMouseSubscriber { get; }

    Task StartAsync(DeviceProfile targetDevice);

    Task StopAsync();

    Task SendKeyAsync(DeviceProfile targetDevice, Key key);

    Task SendKeyboardStateAsync(DeviceProfile targetDevice, IReadOnlyCollection<CapturedKey> pressedKeys);

    Task SendKeyboardReportAsync(DeviceProfile targetDevice, byte[] report, string description);

    Task SendConsumerControlAsync(DeviceProfile targetDevice, ushort usage);

    Task SendPointerAsync(DeviceProfile targetDevice, int x, int y);

    Task SendMouseReportAsync(DeviceProfile targetDevice, sbyte deltaX, sbyte deltaY, byte buttons, sbyte wheel = 0, sbyte hWheel = 0);
}
