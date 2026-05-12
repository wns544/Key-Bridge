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
    private const byte AbsolutePointerInputReportId = 4;
    private const byte OutputReportType = 0x02;
    private const byte InputReportType = 0x01;

    private GattServiceProvider? serviceProvider;
    private GattLocalCharacteristic? inputReportCharacteristic;
    private GattLocalCharacteristic? bootKeyboardInputReportCharacteristic;
    private GattLocalCharacteristic? mouseInputReportCharacteristic;
    private GattLocalCharacteristic? bootMouseInputReportCharacteristic;
    private GattLocalCharacteristic? consumerControlInputReportCharacteristic;
    private GattLocalCharacteristic? absolutePointerInputReportCharacteristic;
    private readonly SemaphoreSlim mouseReportLock = new(1, 1);
    private volatile bool hasKeyboardSubscriber;
    private volatile bool hasMouseSubscriber;
    private const int AbsolutePointerMax = 32767;
    private const int AbsolutePointerCenter = AbsolutePointerMax / 2;
    private const int AbsolutePointerSensitivity = 48;
    private int absolutePointerX = AbsolutePointerCenter;
    private int absolutePointerY = AbsolutePointerCenter;

    public event EventHandler<string>? DiagnosticMessage;
    public event EventHandler<bool>? MouseSubscriberChanged;

    public bool IsRunning { get; private set; }

    public bool HasKeyboardSubscriber => hasKeyboardSubscriber;
    public bool HasMouseSubscriber => hasMouseSubscriber;

    public async Task StartAsync(DeviceProfile targetDevice)
    {
        if (IsRunning) return;
        await CreateHidServiceAsync();
        serviceProvider?.StartAdvertising(new GattServiceProviderAdvertisingParameters { IsConnectable = true, IsDiscoverable = true });
        DiagnosticMessage?.Invoke(this, "BLE HID service is advertising.");
        IsRunning = true;
    }

    public async Task StopAsync()
    {
        if (serviceProvider is not null)
        {
            try { serviceProvider.StopAdvertising(); } catch (Exception ex) { DiagnosticMessage?.Invoke(this, $"Error stopping advertising: {ex.Message}"); }
            finally
            {
                hasKeyboardSubscriber = false;
                if (hasMouseSubscriber)
                {
                    hasMouseSubscriber = false;
                    MouseSubscriberChanged?.Invoke(this, false);
                }
                inputReportCharacteristic = null;
                bootKeyboardInputReportCharacteristic = null;
                mouseInputReportCharacteristic = null;
                bootMouseInputReportCharacteristic = null;
                consumerControlInputReportCharacteristic = null;
                absolutePointerInputReportCharacteristic = null;
                serviceProvider = null;
                DiagnosticMessage?.Invoke(this, "BLE HID service and characteristics cleared.");
            }
        }
        IsRunning = false;
        await Task.CompletedTask;
    }

    public async Task SendKeyAsync(DeviceProfile targetDevice, Key key)
    {
        await SendKeyboardStateAsync(targetDevice, new[] { new CapturedKey(key, KeyInterop.VirtualKeyFromKey(key)) });
        await Task.Delay(15);
        await SendKeyboardStateAsync(targetDevice, Array.Empty<CapturedKey>());
    }

    public async Task SendKeyboardStateAsync(DeviceProfile targetDevice, IReadOnlyCollection<CapturedKey> pressedKeys)
    {
        if (!IsRunning || inputReportCharacteristic is null) return;
        var report = HidKeyboardReport.FromPressedKeys(pressedKeys);
        int inputSubscribers = 0;
        try { inputSubscribers = inputReportCharacteristic.SubscribedClients.Count; } catch { /* Ignore */ }
        DiagnosticMessage?.Invoke(this, $"Sending state [{HidKeyboardReport.DescribePressedKeys(pressedKeys)}]; ModByte: 0x{report[0]:X2}; subscribers: {inputSubscribers}");
        await NotifyKeyboardReportsAsync(report, "state");
    }

    public async Task SendKeyboardReportAsync(DeviceProfile targetDevice, byte[] report, string description)
    {
        if (!IsRunning || inputReportCharacteristic is null) return;
        DiagnosticMessage?.Invoke(this, $"Sending keyboard shortcut [{description}].");
        await NotifyKeyboardReportsAsync(report, "shortcut");
        await Task.Delay(15);
        await NotifyKeyboardReportsAsync(HidKeyboardReport.Empty, "shortcut release");
    }

    public async Task SendConsumerControlAsync(DeviceProfile targetDevice, ushort usage)
    {
        if (!IsRunning || consumerControlInputReportCharacteristic is null) return;
        var report = new byte[] { (byte)(usage & 0xFF), (byte)(usage >> 8) };
        DiagnosticMessage?.Invoke(this, $"Sending consumer usage 0x{usage:X4}");
        await SafeNotifyAsync(consumerControlInputReportCharacteristic, report, "consumer input");
        await Task.Delay(15);
        await SafeNotifyAsync(consumerControlInputReportCharacteristic, new byte[] { 0x00, 0x00 }, "consumer release");
    }

    public async Task SendPointerAsync(DeviceProfile targetDevice, int x, int y)
    {
        if (!IsRunning) return;

        absolutePointerX = NormalizeAbsoluteCoordinate(x);
        absolutePointerY = NormalizeAbsoluteCoordinate(y);

        DiagnosticMessage?.Invoke(this, $"Sending absolute pointer position x={absolutePointerX}, y={absolutePointerY}.");
        await NotifyAbsolutePointerAsync();
    }

    public async Task SendMouseReportAsync(DeviceProfile targetDevice, sbyte deltaX, sbyte deltaY, byte buttons, sbyte wheel = 0, sbyte hWheel = 0)
    {
        if (!IsRunning) return;

        await NotifyRelativeMouseAsync(deltaX, deltaY, buttons, wheel, hWheel);
    }

    private async Task NotifyRelativeMouseAsync(sbyte deltaX, sbyte deltaY, byte buttons, sbyte wheel, sbyte hWheel)
    {
        await mouseReportLock.WaitAsync();
        try
        {
            if (mouseInputReportCharacteristic is not null)
            {
                var report = new byte[] { buttons, unchecked((byte)deltaX), unchecked((byte)deltaY), unchecked((byte)wheel), unchecked((byte)hWheel) };
                await SafeNotifyAsync(mouseInputReportCharacteristic, report, "mouse input");
            }

            if (bootMouseInputReportCharacteristic is not null)
            {
                var bootReport = new byte[] { buttons, unchecked((byte)deltaX), unchecked((byte)deltaY) };
                await SafeNotifyAsync(bootMouseInputReportCharacteristic, bootReport, "boot mouse");
            }
        }
        finally
        {
            mouseReportLock.Release();
        }
    }

    private async Task NotifyAbsolutePointerAsync()
    {
        if (absolutePointerInputReportCharacteristic is null) return;

        var report = CreateAbsolutePointerReport(absolutePointerX, absolutePointerY);
        await SafeNotifyAsync(absolutePointerInputReportCharacteristic, report, "absolute pointer input");
    }

    private static int NormalizeAbsoluteCoordinate(int value)
    {
        if (value is >= 0 and <= 100)
        {
            return Math.Clamp(value * AbsolutePointerMax / 100, 0, AbsolutePointerMax);
        }

        return Math.Clamp(value, 0, AbsolutePointerMax);
    }

    private static byte[] CreateAbsolutePointerReport(int x, int y)
    {
        return new[]
        {
            (byte)(x & 0xFF),
            (byte)((x >> 8) & 0xFF),
            (byte)(y & 0xFF),
            (byte)((y >> 8) & 0xFF)
        };
    }
    private async Task CreateHidServiceAsync()
    {
        var providerResult = await GattServiceProvider.CreateAsync(BluetoothUuidHelper.FromShortId(HidServiceUuid));
        if (providerResult.Error != BluetoothError.Success) throw new InvalidOperationException($"Unable to create HID GATT service: {providerResult.Error}");
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
        absolutePointerInputReportCharacteristic = await CreateAbsolutePointerInputReportCharacteristicAsync();

        inputReportCharacteristic.SubscribedClientsChanged += (_, _) => { hasKeyboardSubscriber = GetSubscribedClientCount(inputReportCharacteristic, "input report") > 0; };
        bootKeyboardInputReportCharacteristic.SubscribedClientsChanged += (_, _) => { /* Log optional */ };
        mouseInputReportCharacteristic.SubscribedClientsChanged += (c, _) => { 
            UpdateMouseSubscriberState();
        };
        bootMouseInputReportCharacteristic.SubscribedClientsChanged += (_, _) => { /* Log optional */ };
        consumerControlInputReportCharacteristic.SubscribedClientsChanged += (_, _) => { /* Log optional */ };
        absolutePointerInputReportCharacteristic.SubscribedClientsChanged += (_, _) => { UpdateMouseSubscriberState(); };
    }

    private void UpdateMouseSubscriberState()
    {
        var connected = GetSubscribedClientCount(mouseInputReportCharacteristic, "mouse input report") > 0
            || GetSubscribedClientCount(absolutePointerInputReportCharacteristic, "absolute pointer input report") > 0;

        if (connected == hasMouseSubscriber)
        {
            return;
        }

        hasMouseSubscriber = connected;
        MouseSubscriberChanged?.Invoke(this, connected);
    }

    private int GetSubscribedClientCount(GattLocalCharacteristic? characteristic, string name)
    {
        if (characteristic is null) return 0;
        try { return characteristic.SubscribedClients.Count; }
        catch (Exception ex) { DiagnosticMessage?.Invoke(this, $"Unable to read {name} subscribers: {ex.Message}"); return 0; }
    }

    private async Task CreateReadCharacteristicAsync(ushort shortUuid, string name, byte[] value)
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read, ReadProtectionLevel = HidProtectionLevel, UserDescription = name, StaticValue = ToBuffer(value) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(shortUuid), parameters);
        ThrowIfBluetoothError(result.Error, name);
    }

    private async Task CreateProtocolModeCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.WriteWithoutResponse, ReadProtectionLevel = HidProtectionLevel, WriteProtectionLevel = HidProtectionLevel, UserDescription = "Protocol Mode", StaticValue = ToBuffer(new byte[] { 0x01 }) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(ProtocolModeUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Protocol Mode");
    }

    private async Task CreateHidControlPointCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.WriteWithoutResponse, WriteProtectionLevel = HidProtectionLevel, UserDescription = "HID Control Point" };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(HidControlPointUuid), parameters);
        ThrowIfBluetoothError(result.Error, "HID Control Point");
    }

    private async Task<GattLocalCharacteristic> CreateInputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, ReadProtectionLevel = HidProtectionLevel, UserDescription = "Keyboard Input Report", StaticValue = ToBuffer(HidKeyboardReport.Empty) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(ReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Keyboard Input Report");
        var descriptorParameters = new GattLocalDescriptorParameters { ReadProtectionLevel = HidProtectionLevel, StaticValue = ToBuffer(new byte[] { InputReportId, InputReportType }) };
        await result.Characteristic.CreateDescriptorAsync(BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid), descriptorParameters);
        return result.Characteristic;
    }

    private async Task CreateOutputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse, ReadProtectionLevel = HidProtectionLevel, WriteProtectionLevel = HidProtectionLevel, UserDescription = "Keyboard Output Report", StaticValue = ToBuffer(new byte[] { 0x00 }) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(ReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Keyboard Output Report");
        var descriptorParameters = new GattLocalDescriptorParameters { ReadProtectionLevel = HidProtectionLevel, StaticValue = ToBuffer(new byte[] { InputReportId, OutputReportType }) };
        await result.Characteristic.CreateDescriptorAsync(BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid), descriptorParameters);
    }

    private async Task CreateBootKeyboardOutputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Write | GattCharacteristicProperties.WriteWithoutResponse, ReadProtectionLevel = HidProtectionLevel, WriteProtectionLevel = HidProtectionLevel, UserDescription = "Boot Keyboard Output Report", StaticValue = ToBuffer(new byte[] { 0x00 }) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(BootKeyboardOutputReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Boot Keyboard Output Report");
    }

    private async Task<GattLocalCharacteristic> CreateBootKeyboardInputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, ReadProtectionLevel = HidProtectionLevel, UserDescription = "Boot Keyboard Input Report", StaticValue = ToBuffer(HidKeyboardReport.Empty) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(BootKeyboardInputReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Boot Keyboard Input Report");
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> CreateMouseInputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, ReadProtectionLevel = HidProtectionLevel, UserDescription = "Mouse Input Report", StaticValue = ToBuffer(new byte[] { 0x00, 0x00, 0x00, 0x00, 0x00 }) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(ReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Mouse Input Report");
        var descriptorParameters = new GattLocalDescriptorParameters { ReadProtectionLevel = HidProtectionLevel, StaticValue = ToBuffer(new byte[] { MouseInputReportId, InputReportType }) };
        await result.Characteristic.CreateDescriptorAsync(BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid), descriptorParameters);
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> CreateBootMouseInputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, ReadProtectionLevel = HidProtectionLevel, UserDescription = "Boot Mouse Input Report", StaticValue = ToBuffer(new byte[] { 0x00, 0x00, 0x00 }) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(BootMouseInputReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Boot Mouse Input Report");
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> CreateConsumerControlInputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, ReadProtectionLevel = HidProtectionLevel, UserDescription = "Consumer Control Input Report", StaticValue = ToBuffer(new byte[] { 0x00, 0x00 }) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(ReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Consumer Control Input Report");
        var descriptorParameters = new GattLocalDescriptorParameters { ReadProtectionLevel = HidProtectionLevel, StaticValue = ToBuffer(new byte[] { ConsumerControlInputReportId, InputReportType }) };
        await result.Characteristic.CreateDescriptorAsync(BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid), descriptorParameters);
        return result.Characteristic;
    }

    private async Task<GattLocalCharacteristic> CreateAbsolutePointerInputReportCharacteristicAsync()
    {
        EnsureProvider();
        var parameters = new GattLocalCharacteristicParameters { CharacteristicProperties = GattCharacteristicProperties.Read | GattCharacteristicProperties.Notify, ReadProtectionLevel = HidProtectionLevel, UserDescription = "Absolute Pointer Input Report", StaticValue = ToBuffer(CreateAbsolutePointerReport(AbsolutePointerCenter, AbsolutePointerCenter)) };
        var result = await serviceProvider!.Service.CreateCharacteristicAsync(BluetoothUuidHelper.FromShortId(ReportUuid), parameters);
        ThrowIfBluetoothError(result.Error, "Absolute Pointer Input Report");
        var descriptorParameters = new GattLocalDescriptorParameters { ReadProtectionLevel = HidProtectionLevel, StaticValue = ToBuffer(new byte[] { AbsolutePointerInputReportId, InputReportType }) };
        await result.Characteristic.CreateDescriptorAsync(BluetoothUuidHelper.FromShortId(ReportReferenceDescriptorUuid), descriptorParameters);
        return result.Characteristic;
    }

    private async Task SafeNotifyAsync(GattLocalCharacteristic? characteristic, byte[] data, string name)
    {
        if (characteristic == null) return;
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            var notifyTask = characteristic.NotifyValueAsync(ToBuffer(data)).AsTask(cts.Token);
            var results = await notifyTask;
            if (results.Count == 0)
            {
                if (characteristic == inputReportCharacteristic) hasKeyboardSubscriber = false;
                if (characteristic == mouseInputReportCharacteristic || characteristic == absolutePointerInputReportCharacteristic) hasMouseSubscriber = false;
            }
        }
        catch (OperationCanceledException)
        {
            DiagnosticMessage?.Invoke(this, $"Warning: {name} notification timed out (15ms).");
            if (characteristic == inputReportCharacteristic) hasKeyboardSubscriber = false;
            if (characteristic == mouseInputReportCharacteristic || characteristic == absolutePointerInputReportCharacteristic) hasMouseSubscriber = false;
        }
        catch (Exception ex) { DiagnosticMessage?.Invoke(this, $"Error in {name} notification: {ex.Message}"); }
    }

    private async Task NotifyKeyboardReportsAsync(byte[] report, string phase)
    {
        await SafeNotifyAsync(inputReportCharacteristic, report, $"keyboard input ({phase})");
        await SafeNotifyAsync(bootKeyboardInputReportCharacteristic, report, $"boot keyboard ({phase})");
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
            0x05, 0x01, 0x09, 0x30, 0x09, 0x31, 0x09, 0x38, 0x09, 0x3C,
            0x15, 0x81, 0x25, 0x7F, 0x75, 0x08, 0x95, 0x02,
            0x81, 0x06,
            0xC0, 0xC0,
            0x05, 0x0C, 0x09, 0x01, 0xA1, 0x01, 0x85, ConsumerControlInputReportId,
            0x15, 0x00, 0x26, 0xFF, 0x03, 0x19, 0x00, 0x2A,
            0xFF, 0x03, 0x75, 0x10, 0x95, 0x01, 0x81, 0x00,
            0xC0,
            0x05, 0x01, 0x09, 0x02, 0xA1, 0x01, 0x85, AbsolutePointerInputReportId,
            0x09, 0x01, 0xA1, 0x00,
            0x09, 0x30, 0x09, 0x31,
            0x15, 0x00, 0x26, 0xFF, 0x7F, 0x75, 0x10, 0x95, 0x02,
            0x81, 0x02,
            0xC0, 0xC0
        };
    }

    private void EnsureProvider() { if (serviceProvider is null) throw new InvalidOperationException("HID GATT service has not been created."); }
    private static void ThrowIfBluetoothError(BluetoothError error, string component) { if (error != BluetoothError.Success) throw new InvalidOperationException($"{component} failed: {error}"); }
    private static IBuffer ToBuffer(byte[] bytes) { var writer = new DataWriter(); writer.WriteBytes(bytes); return writer.DetachBuffer(); }
    private static string FormatBytes(byte[] bytes) => string.Join(" ", bytes.Select(v => v.ToString("X2")));
}
