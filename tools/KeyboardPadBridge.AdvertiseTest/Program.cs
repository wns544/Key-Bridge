using KeyboardPadBridge.Models;
using KeyboardPadBridge.Services;

var bridge = new BluetoothHidBridgeService();
var target = new DeviceProfile("Pairing Test", "Tablet", "F8");

var durationSeconds = args.Length > 0 && int.TryParse(args[0], out var parsedSeconds)
    ? parsedSeconds
    : 300;

Console.WriteLine($"Starting BLE HID keyboard advertising for {durationSeconds} seconds.");
Console.WriteLine("Open iPad Settings > Bluetooth and look for this PC/BLE keyboard candidate.");

await bridge.StartAsync(target);

try
{
    for (var secondsLeft = durationSeconds; secondsLeft > 0; secondsLeft -= 5)
    {
        Console.WriteLine($"{secondsLeft}s remaining...");
        await Task.Delay(TimeSpan.FromSeconds(5));
    }
}
finally
{
    await bridge.StopAsync();
    Console.WriteLine("Advertising stopped.");
}
