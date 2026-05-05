using KeyboardPadBridge.Models;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Storage.Streams;
using System.Windows.Input;

namespace KeyboardPadBridge.Services;

public sealed class BluetoothHidBridgeService : IHidBridgeService
{
    private const GattProtectionLevel HidProtectionLevel = GattProtectionLevel.EncryptionRequired;
    private const ushort HidServiceUuid = 0x1812;
    private const ushort HidInformationUuid = 0x2A4A;
    private const ushort ReportMapUuid = 0x2A4B;
    private const ushort HidControlPointUuid = 0x2A4C;
    private const ushort ReportUuid = 0x2A4D;
    private const ushort ProtocolModeUuid = 0x2A4E;
    private const ushort BootKeyboardInputReportUuid = 0x2A22;
    private const ushort BootKeyboardOutputReportUuid = 0x2A32;
    private const ushort BootMouseInputReportUuid = 0x2A33;
    private const ushort ReportReferenceDescriptorUuid = 0x2908;
    private const byte InputReportId = 1;
    private const byte MouseInputReportId = 2;
    private const byte ConsumerControlInputReportId = 3;
    private const byte OutputReportType = 0x02;
    private const byte InputReportType = 0x01;

    private GattServiceProvider? serviceProvider;
    private GattLocalCharacteristic? inputReportCharacteristic;
    private GattLocalCharacteristic? bootKeyboardInputReportCharacteristic;
    private GattLocalCharacteristic? mouseInputReportCharacteristic;
    private GattLocalCharacteristic? bootMouseInputReportCharacteristic;
    private GattLocalCharacteristic? consumerControlInputReportCharacteristic;

    public event EventHandler<string>? DiagnosticMessage;

    public bool IsRunning { get; private set; }

    public async Task StartAsync(DeviceProfile targetDevice)
    {
        if (IsRunning)
        {
            return;
        }

        await CreateHidServiceAsync();

        serviceProvider?.StartAdvertising(new GattServiceProviderAdvertisingParameters
        {
            IsConnectable = true,
            IsDiscoverable = true
        });

        DiagnosticMessage?.Invoke(this, "BLE HID service is advertising.");
        IsRunning = true;
    }

    public Task StopAsync()
    {
        if (serviceProvider is not null)
        {
            serviceProvider.StopAdvertising();
            serviceProvider = null;
            inputReportCharacteristic = null;
            bootKeyboardInputReportCharacteristic = null;
            mouseInputReportCharacteristic = null;
            bootMouseInputReportCharacteristic = null;
            consumerControlInputReportCharacteristic = null;
            DiagnosticMessage?.Invoke(this, "BLE HID advertising stopped.");
        }

        IsRunning = false;
        return Task.CompletedTask;
    }

    public async Task SendKeyAsync(DeviceProfile targetDevice, Key key)
    {
        await SendKeyboardStateAsync(targetDevice, new[] { new CapturedKey(key, KeyInterop.VirtualKeyFromKey(key)) });
        await Task.Delay(12);
        await SendKeyboardStateAsync(targetDevice, Array.Empty<CapturedKey>());
    }

    public async Task SendKeyboardStateAsync(DeviceProfile targetDevice, IReadOnlyCollection<CapturedKey> pressedKeys)
    {
        if (!IsRunning || inputReportCharacteristic is null)
        {
            return;
        }

        var report = HidKeyboardReport.FromPressedKeys(pressedKeys);
        var inputSubscribers = inputReportCharacteristic.SubscribedClients.Count;
        var bootSubscribers = bootKeyboardInputReportCharacteristic?.SubscribedClients.Count ?? 0;
        DiagnosticMessage?.Invoke(
            this,
            $"Sending state [{HidKeyboardReport.DescribePressedKeys(pressedKeys)}]; input subscribers: {inputSubscribers}; boot subscribers: {bootSubscribers}.");

        await NotifyKeyboardReportsAsync(report, "state");
    }

    public async Task SendConsumerControlAsync(DeviceProfile targetDevice, ushort usage)
    {
        if (!IsRunning || consumerControlInputReportCharacteristic is null)
        {
            return;
        }

        var report = new[] { (byte)(usage & 0xFF), (byte)(usage >> 8) };
        var inputSubscribers = consumerControlInputReportCharacteristic.SubscribedClients.Count;
        DiagnosticMessage?.Invoke(this, $"Sending consumer usage 0x{usage:X4}; subscribers: {inputSubscribers}.");
        var inputResults = await consumerControlInputReportCharacteristic.NotifyValueAsync(ToBuffer(report));
        DiagnosticMessage?.Invoke(this, FormatNotificationResults(inputResults, "consumer input"));

        await Task.Delay(12);

        var releaseResults = await consumerControlInputReportCharacteristic.NotifyValueAsync(ToBuffer(new byte[] { 0x00, 0x00 }));
        DiagnosticMessage?.Invoke(this, FormatNotificationResults(releaseResults, "consumer release"));
    }

    public Task SendPointerAsync(DeviceProfile targetDevice, int x, int y)
    {
        return Task.CompletedTask;
    }

    public async Task SendMouseReportAsync(DeviceProfile targetDevice, sbyte deltaX, sbyte deltaY, byte buttons, sbyte wheel = 0)
    {
        if (!IsRunning)
        {
            return;
        }

        if (mouseInputReportCharacteristic is not null)
        {
            var report = new byte[] { buttons, unchecked((byte)deltaX), unchecked((byte)deltaY), unchecked((byte)wheel) };
            await mouseInputReportCharacteristic.NotifyValueAsync(ToBuffer(report));
        }

        if (bootMouseInputReportCharacteristic is not null)
        {
            var bootReport = new byte[] { buttons, unchecked((byte)deltaX), unchecked((byte)deltaY) };
            await bootMouseInputReportCharacteristic.NotifyValueAsync(ToBuffer(bootReport));
        }
    }

    private async Task CreateHidServiceAsync()
    {
        var providerResult = await GattServiceProvider.CreateAsync(BluetoothUuidHelper.FromShortId(HidServiceUuid));
        if (providerResult.Error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"Unable to create HID GATT service: {providerResult.Error}");
        }

        serviceProvider = providerResult.ServiceProvider;

        await CreateReadCharacteristicAsync(HidInformationUuid, "HID Information", new byte[] { 0x11, 0x01, 0x00, 0x02 });
        await CreateReadCharacteristicAsync(ReportMapUuid, "Report Map", CreateKeyboardReportMap());
        await CreateProtocolModeCharacteristicAsync();
        await CreateHidControlPointCharacteristicAsync();
        await CreateOutputReportCharacteristicAsync();
        await CreateBootKeyboardOutputReportCharacteristicAsync();
        inputReportCharacteristic = await CreateInputReportCharacteristicAsync();
        bootKeyboardInputReportCharacteristic = await CreateBootKeyboardInputReportCharacteristicAsync();
        mouseInputReportCharacteristic = await CreateMouseInputReportCharacteristicAsync();
        bootMouseInputReportCharacteristic = await CreateBootMouseInputReportCharacteristicAsync();
        consumerControlInputReportCharacteristic = await CreateConsumerControlInputReportCharacteristicAsync();

        inputReportCharacteristic.SubscribedClientsChanged += (_, _) =>
        {
            DiagnosticMessage?.Invoke(
                this,
                $"Input report subscribers: {inputReportCharacteristic.SubscribedClients.Count}.");
        };

        bootKeyboardInputReportCharacteristic.SubscribedClientsChanged += (_, _) =>
        {
            DiagnosticMessage?.Invoke(
                this,
                $"Boot keyboard subscribers: {bootKeyboardInputReportCharacteristic.SubscribedClients.Count}.");
        };

        mouseInputReportCharacteristic.SubscribedClientsChanged += (_, _) =>
        {
            DiagnosticMessage?.Invoke(
                this,
                $"Mouse input subscribers: {mouseInputReportCharacteristic.SubscribedClients.Count}.");
        };

        bootMouseInputReportCharacteristic.SubscribedClientsChanged += (_, _) =>
        {
            DiagnosticMessage?.Invoke(
                this,
                $"Boot mouse subscribers: {bootMouseInputReportCharacteristic.SubscribedClients.Count}.");
        };

        consumerControlInputReportCharacteristic.SubscribedClientsChanged += (_, _) =>
        {
            DiagnosticMessage?.Invoke(
                this,
                $"Consumer control subscribers: {consumerControlInputReportCharacteristic.SubscribedClients.Count}.");
        };
    }

    private async Task CreateReadCharacteristicAsync(ushort shortUuid, string name, byte[] value)
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read,
            ReadProtectionLevel = HidProtectionLevel,
            UserDescription = name,
            StaticValue = ToBuffer(value)
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(shortUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, name);
    }

    private async Task CreateProtocolModeCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse,
            ReadProtectionLevel = HidProtectionLevel,
            WriteProtectionLevel = HidProtectionLevel,
            UserDescription = "Protocol Mode",
            StaticValue = ToBuffer(new byte[] { 0x01 })
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(ProtocolModeUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Protocol Mode");
    }

    private async Task CreateHidControlPointCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse,
            WriteProtectionLevel = HidProtectionLevel,
            UserDescription = "HID Control Point"
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(HidControlPointUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "HID Control Point");
    }

    private async Task<GattLocalCharacteristic> CreateInputReportCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = HidProtectionLevel,
            UserDescription = "Keyboard Input Report",
            StaticValue = ToBuffer(HidKeyboardReport.Empty)
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(ReportUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Keyboard Input Report");

        var descriptorParameters = new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = HidProtectionLevel,
            StaticValue = ToBuffer(new byte[] { InputReportId, InputReportType })
        };

        var descriptorResult = await result.Characteristic.CreateDescriptorAsync(
            BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid),
            descriptorParameters);

        ThrowIfBluetoothError(descriptorResult.Error, "Report Reference Descriptor");
        return result.Characteristic;
    }

    private async Task CreateOutputReportCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
            ReadProtectionLevel = HidProtectionLevel,
            WriteProtectionLevel = HidProtectionLevel,
            UserDescription = "Keyboard Output Report",
            StaticValue = ToBuffer(new byte[] { 0x00 })
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(ReportUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Keyboard Output Report");

        var descriptorParameters = new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = HidProtectionLevel,
            StaticValue = ToBuffer(new byte[] { InputReportId, OutputReportType })
        };

        var descriptorResult = await result.Characteristic.CreateDescriptorAsync(
            BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid),
            descriptorParameters);

        ThrowIfBluetoothError(descriptorResult.Error, "Output Report Reference Descriptor");
        DiagnosticMessage?.Invoke(this, "Keyboard Output Report characteristic created.");
    }

    private async Task CreateBootKeyboardOutputReportCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse,
            ReadProtectionLevel = HidProtectionLevel,
            WriteProtectionLevel = HidProtectionLevel,
            UserDescription = "Boot Keyboard Output Report",
            StaticValue = ToBuffer(new byte[] { 0x00 })
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(BootKeyboardOutputReportUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Boot Keyboard Output Report");
        DiagnosticMessage?.Invoke(this, "Boot Keyboard Output Report characteristic created.");
    }

    private async Task<GattLocalCharacteristic> CreateBootKeyboardInputReportCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = HidProtectionLevel,
            UserDescription = "Boot Keyboard Input Report",
            StaticValue = ToBuffer(HidKeyboardReport.Empty)
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(BootKeyboardInputReportUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Boot Keyboard Input Report");
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> CreateMouseInputReportCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = HidProtectionLevel,
            UserDescription = "Mouse Input Report",
            StaticValue = ToBuffer(new byte[] { 0x00, 0x00, 0x00, 0x00 })
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(ReportUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Mouse Input Report");

        var descriptorParameters = new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = HidProtectionLevel,
            StaticValue = ToBuffer(new byte[] { MouseInputReportId, InputReportType })
        };

        var descriptorResult = await result.Characteristic.CreateDescriptorAsync(
            BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid),
            descriptorParameters);

        ThrowIfBluetoothError(descriptorResult.Error, "Mouse Report Reference Descriptor");
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> CreateBootMouseInputReportCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = HidProtectionLevel,
            UserDescription = "Boot Mouse Input Report",
            StaticValue = ToBuffer(new byte[] { 0x00, 0x00, 0x00 })
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(BootMouseInputReportUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Boot Mouse Input Report");
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> CreateConsumerControlInputReportCharacteristicAsync()
    {
        EnsureProvider();

        var parameters = new GattLocalCharacteristicParameters
        {
            CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify,
            ReadProtectionLevel = HidProtectionLevel,
            UserDescription = "Consumer Control Input Report",
            StaticValue = ToBuffer(new byte[] { 0x00, 0x00 })
        };

        var result = await serviceProvider!.Service.CreateCharacteristicAsync(
            BluetoothUuidHelper.FromShortId(ReportUuid),
            parameters);

        ThrowIfBluetoothError(result.Error, "Consumer Control Input Report");

        var descriptorParameters = new GattLocalDescriptorParameters
        {
            ReadProtectionLevel = HidProtectionLevel,
            StaticValue = ToBuffer(new byte[] { ConsumerControlInputReportId, InputReportType })
        };

        var descriptorResult = await result.Characteristic.CreateDescriptorAsync(
            BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid),
            descriptorParameters);

        ThrowIfBluetoothError(descriptorResult.Error, "Consumer Control Report Reference Descriptor");
        return result.Characteristic;
    }

    private async Task NotifyKeyboardReportsAsync(byte[] report, string phase)
    {
        if (inputReportCharacteristic is not null)
        {
            DiagnosticMessage?.Invoke(this, $"Input report bytes: {FormatBytes(report)}.");
            var inputResults = await inputReportCharacteristic.NotifyValueAsync(ToBuffer(report));
            DiagnosticMessage?.Invoke(this, FormatNotificationResults(inputResults, $"input {phase}"));
        }

        if (bootKeyboardInputReportCharacteristic is not null)
        {
            DiagnosticMessage?.Invoke(this, $"Boot report bytes: {FormatBytes(report)}.");
            var bootResults = await bootKeyboardInputReportCharacteristic.NotifyValueAsync(ToBuffer(report));
            DiagnosticMessage?.Invoke(this, FormatNotificationResults(bootResults, $"boot {phase}"));
        }
    }

    private static byte[] CreateKeyboardReportMap()
    {
        return new byte[]
        {
            0x05, 0x01, 0x09, 0x06, 0xA1, 0x01, 0x85, InputReportId,
            0x05, 0x07, 0x19, 0xE0, 0x29, 0xE7, 0x15, 0x00,
            0x25, 0x01, 0x75, 0x01, 0x95, 0x08, 0x81, 0x02,
            0x95, 0x01, 0x75, 0x08, 0x81, 0x01, 0x95, 0x05,
            0x75, 0x01, 0x05, 0x08, 0x19, 0x01, 0x29, 0x05,
            0x91, 0x02, 0x95, 0x01, 0x75, 0x03, 0x91, 0x01,
            0x95, 0x06, 0x75, 0x08, 0x15, 0x00, 0x25, 0x65,
            0x05, 0x07, 0x19, 0x00, 0x29, 0x65, 0x81, 0x00,
            0xC0,

            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, MouseInputReportId,
            0x09, 0x01, 0xA1, 0x00,
            0x05, 0x09, 0x19, 0x01, 0x29, 0x03, 0x15, 0x00,
            0x25, 0x01, 0x95, 0x03, 0x75, 0x01, 0x81, 0x02,
            0x95, 0x01, 0x75, 0x05, 0x81, 0x01,
            0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38,
            0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x03,
            0x81, 0x06, 0xC0, 0xC0,

            0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, ConsumerControlInputReportId,
            0x15, 0x00, 0x26, 0xFF, 0x03, 0x19, 0x00, 0x2A,
            0xFF, 0x03, 0x75, 0x10, 0x95, 0x01, 0x81, 0x00,
            0xC0
        };
    }

    private void EnsureProvider()
    {
        if (serviceProvider is null)
        {
            throw new InvalidOperationException("HID GATT service has not been created.");
        }
    }

    private static void ThrowIfBluetoothError(BluetoothError error, string component)
    {
        if (error != BluetoothError.Success)
        {
            throw new InvalidOperationException($"{component} failed: {error}");
        }
    }

    private static string FormatNotificationResults(
        IReadOnlyList<GattClientNotificationResult> results,
        string phase)
    {
        if (results.Count == 0)
        {
            return $"Notify {phase}: no subscribed clients.";
        }

        return $"Notify {phase}: {string.Join(", ", results.Select(result => result.Status))}.";
    }

    private static IBuffer ToBuffer(byte[] bytes)
    {
        var writer = new DataWriter();
        writer.WriteBytes(bytes);
        return writer.DetachBuffer();
    }

    private static string FormatBytes(byte[] bytes)
    {
        return string.Join(" ", bytes.Select(value => value.ToString("X2")));
    }
}
