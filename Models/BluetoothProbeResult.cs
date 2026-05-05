namespace KeyboardPadBridge.Models;

public sealed record BluetoothProbeResult(
    bool CanUseBluetoothApis,
    bool CanAdvertiseBle,
    bool CanCreateHidGattService,
    IReadOnlyList<string> Messages)
{
    public string Summary
    {
        get
        {
            if (CanAdvertiseBle && CanCreateHidGattService)
            {
                return "BLE HID path looks testable";
            }

            if (CanUseBluetoothApis)
            {
                return "Bluetooth APIs available, HID path uncertain";
            }

            return "Bluetooth APIs unavailable";
        }
    }
}
