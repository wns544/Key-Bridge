using KeyboardPadBridge.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Radios;
using Windows.Foundation.Metadata;
using Windows.Storage.Streams;

namespace KeyboardPadBridge.Services;

public sealed class BluetoothCapabilityProbe
{
    private const ushort HidServiceUuid = 0x1812;

    public async Task<BluetoothProbeResult> RunAsync()
    {
        var messages = new List<string>
        {
            $"OS: {Environment.OSVersion.VersionString}",
        };

        var hasRadioApi = ApiInformation.IsTypePresent("Windows.Devices.Radios.Radio");
        var hasBlePublisherApi = ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher");
        var hasGattProviderApi = ApiInformation.IsTypePresent("Windows.Devices.Bluetooth.GenericAttributeProfile.GattServiceProvider");

        messages.Add($"Radio API: {(hasRadioApi ? "present" : "missing")}");
        messages.Add($"BLE advertisement API: {(hasBlePublisherApi ? "present" : "missing")}");
        messages.Add($"GATT provider API: {(hasGattProviderApi ? "present" : "missing")}");

        var canUseBluetoothApis = hasRadioApi && hasBlePublisherApi && hasGattProviderApi;
        var canAdvertiseBle = false;
        var canCreateHidGattService = false;

        if (!canUseBluetoothApis)
        {
            messages.Add("Result: Windows Bluetooth APIs needed for a pure software backend are not all available.");
            return new BluetoothProbeResult(false, false, false, messages);
        }

        await ProbeBluetoothRadiosAsync(messages);
        canAdvertiseBle = await ProbeBleAdvertisementAsync(messages);
        canCreateHidGattService = await ProbeHidGattProviderAsync(messages);

        if (canAdvertiseBle && canCreateHidGattService)
        {
            messages.Add("Decision: try a Windows BLE HID backend next.");
        }
        else
        {
            messages.Add("Decision: keep ESP32 bridge as the safer fallback path.");
        }

        return new BluetoothProbeResult(canUseBluetoothApis, canAdvertiseBle, canCreateHidGattService, messages);
    }

    private static async Task ProbeBluetoothRadiosAsync(ICollection<string> messages)
    {
        try
        {
            var radios = await Radio.GetRadiosAsync();
            var bluetoothRadios = radios.Where(radio => radio.Kind == RadioKind.Bluetooth).ToList();

            messages.Add($"Bluetooth radios: {bluetoothRadios.Count}");

            foreach (var radio in bluetoothRadios)
            {
                messages.Add($"Radio '{radio.Name}': {radio.State}");
            }
        }
        catch (Exception ex)
        {
            messages.Add($"Radio probe failed: {ex.GetType().Name} - {ex.Message}");
        }
    }

    private static async Task<bool> ProbeBleAdvertisementAsync(ICollection<string> messages)
    {
        var statusChanges = new List<string>();
        using var statusChanged = new ManualResetEventSlim(false);
        var publisher = new BluetoothLEAdvertisementPublisher();
        publisher.Advertisement.ManufacturerData.Add(new BluetoothLEManufacturerData
        {
            CompanyId = 0xFFFF,
            Data = CreateSingleByteBuffer(0x01)
        });

        publisher.StatusChanged += (_, args) =>
        {
            statusChanges.Add(args.Status.ToString());
            statusChanged.Set();
        };

        try
        {
            publisher.Start();
            await Task.Run(() => statusChanged.Wait(TimeSpan.FromSeconds(2)));

            messages.Add($"BLE publisher status: {publisher.Status}");
            foreach (var status in statusChanges.Distinct())
            {
                messages.Add($"BLE publisher observed: {status}");
            }

            return publisher.Status is BluetoothLEAdvertisementPublisherStatus.Started
                or BluetoothLEAdvertisementPublisherStatus.Waiting;
        }
        catch (Exception ex)
        {
            messages.Add($"BLE advertisement probe failed: {ex.GetType().Name} - {ex.Message}");
            return false;
        }
        finally
        {
            publisher.Stop();
        }
    }

    private static async Task<bool> ProbeHidGattProviderAsync(ICollection<string> messages)
    {
        try
        {
            var hidServiceId = BluetoothUuidHelper.FromShortId(HidServiceUuid);
            var providerResult = await GattServiceProvider.CreateAsync(hidServiceId);
            messages.Add($"HID GATT provider create: {providerResult.Error}");

            if (providerResult.Error != BluetoothError.Success)
            {
                return false;
            }

            var protocolModeParameters = new GattLocalCharacteristicParameters
            {
                CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
                UserDescription = "Protocol Mode"
            };
            protocolModeParameters.StaticValue = CreateSingleByteBuffer(1);

            var characteristicResult = await providerResult.ServiceProvider.Service.CreateCharacteristicAsync(
                BluetoothUuidHelper.FromShortId(0x2A4E),
                protocolModeParameters);

            messages.Add($"HID Protocol Mode characteristic: {characteristicResult.Error}");
            return characteristicResult.Error == BluetoothError.Success;
        }
        catch (Exception ex)
        {
            messages.Add($"HID GATT probe failed: {ex.GetType().Name} - {ex.Message}");
            return false;
        }
    }

    private static IBuffer CreateSingleByteBuffer(byte value)
    {
        var writer = new DataWriter();
        writer.WriteByte(value);
        return writer.DetachBuffer();
    }
}
