using KeyboardPadBridge.Models;
using System.Diagnostics;
using System.Windows.Input;

namespace KeyboardPadBridge.Services;

public sealed class SimulatedHidBridgeService : IHidBridgeService
{
    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<bool>? MouseSubscriberChanged;

    public bool IsRunning { get; private set; }

    public bool HasKeyboardSubscriber => IsRunning;
    public bool HasMouseSubscriber => IsRunning;

    public Task StartAsync(DeviceProfile targetDevice)
    {
        IsRunning = true;
        DiagnosticMessage?.Invoke(this, $"Simulated HID bridge started for {targetDevice.Name}.");
        Debug.WriteLine($"Simulated HID bridge started for {targetDevice.Name}.");
        return Task.CompletedTask;
    }

    public Task StopAsync()
    {
        IsRunning = false;
        DiagnosticMessage?.Invoke(this, "Simulated HID bridge stopped.");
        Debug.WriteLine("Simulated HID bridge stopped.");
        return Task.CompletedTask;
    }

    public Task SendKeyAsync(DeviceProfile targetDevice, Key key)
    {
        Debug.WriteLine($"Key {key} would be sent to {targetDevice.Name}.");
        return Task.CompletedTask;
    }

    public Task SendKeyboardStateAsync(DeviceProfile targetDevice, IReadOnlyCollection<CapturedKey> pressedKeys)
    {
        Debug.WriteLine($"Keys {string.Join("+", pressedKeys.Select(key => key.Key))} would be sent to {targetDevice.Name}.");
        return Task.CompletedTask;
    }

    public Task SendKeyboardReportAsync(DeviceProfile targetDevice, byte[] report, string description)
    {
        Debug.WriteLine($"Keyboard shortcut {description} would be sent to {targetDevice.Name}: {string.Join(" ", report.Select(value => value.ToString("X2")))}.");
        return Task.CompletedTask;
    }

    public Task SendConsumerControlAsync(DeviceProfile targetDevice, ushort usage)
    {
        Debug.WriteLine($"Consumer usage 0x{usage:X4} would be sent to {targetDevice.Name}.");
        return Task.CompletedTask;
    }

    public Task SendPointerAsync(DeviceProfile targetDevice, int x, int y)
    {
        Debug.WriteLine($"Pointer {x},{y} would be sent to {targetDevice.Name}.");
        return Task.CompletedTask;
    }

    public Task SendMouseReportAsync(DeviceProfile targetDevice, sbyte deltaX, sbyte deltaY, byte buttons, sbyte wheel = 0, sbyte hWheel = 0)
    {
        Debug.WriteLine($"Mouse dx={deltaX}, dy={deltaY}, buttons={buttons}, wheel={wheel}, hWheel={hWheel} would be sent to {targetDevice.Name}.");
        return Task.CompletedTask;
    }
}
