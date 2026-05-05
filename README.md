# KeyBridge

Windows laptop keyboard and pointer bridge prototype for tablets such as iPad.

This project is an independent app inspired by the workflow of using a PC keyboard/mouse as input for another device. It is not a copy of ACROSS code, UI, branding, or assets.

## Current state

- WPF desktop app targeting `net10.0-windows`
- BLE HID session panel without mock target devices
- Held-key state reports for modifiers, shortcuts, and special-key combinations
- Windows-style Ctrl shortcut translation into iPad Command shortcuts, including copy, paste, cut, undo/redo, select all, find, save, address/search focus, new tab, close tab, refresh, print, and basic text formatting
- Hangul toggle mapping through hardware-keyboard language switching (`Ctrl+Space`)
- App/window icon generated from `Assets/keybridge-icon-source.png`
- Windows-style command layer: use familiar shortcuts such as `Ctrl+L`, `Ctrl+C`, `Ctrl+V`, and `Ctrl+A`; KeyBridge translates them into iPad `Command` shortcuts. Windows reserves `Win+L`, so use `Ctrl+L` for iPad search/address focus.
- Global keyboard and pointer capture through low-level Windows hooks
- Mouse signal switch for enabling/disabling pointer forwarding separately from keyboard capture
- Emergency stop with `Ctrl+Alt+Esc`
- Global bridge toggle with `Ctrl+\``: starts KeyBridge when idle, stops it when active
- Optional local keystroke blocking while capture is running
- Bluetooth capability probe for checking whether a pure Windows BLE HID backend is worth pursuing
- Experimental BLE HID keyboard service advertising
- Keyboard input/output and boot keyboard input/output report characteristics for the experimental BLE HID backend
- Experimental BLE HID mouse input and boot mouse input report characteristics, controlled by the `Mouse signal` switch
- Encrypted GATT characteristic protection for HID-style pairing tests

## Important limitation

The current backend is experimental. It creates a BLE HID keyboard GATT service and starts advertising, but iPad pairing behavior still needs hands-on testing.

Actual iPad pairing requires one of these backend paths:

- Windows Bluetooth HID peripheral implementation, if the target Windows Bluetooth stack and hardware expose the required role.
- A custom driver or low-level Bluetooth stack component.
- A companion hardware bridge, such as a small BLE HID device controlled by the Windows app.

## Run

```powershell
dotnet run
```

Then click `Start Bridge` and use the keyboard/mouse in any Windows app. The input event log should update in Keyboard Pad Bridge.

Use `Ctrl+Alt+Esc` to stop capture immediately.

Click `Run Probe` to check the current Windows Bluetooth API, BLE advertising, and HID GATT service capability.

You can also run the probe from the terminal:

```powershell
dotnet run --project .\tools\KeyboardPadBridge.Probe\KeyboardPadBridge.Probe.csproj
```

To run only the BLE HID advertising test for iPad pairing:

```powershell
dotnet run --project .\tools\KeyboardPadBridge.AdvertiseTest\KeyboardPadBridge.AdvertiseTest.csproj
```

While it is running, open iPad Settings > Bluetooth and look for a new keyboard/BLE device candidate. Windows may expose it using the PC Bluetooth name rather than the app name.

## Project layout

- `MainWindow.xaml`: desktop UI
- `MainWindow.xaml.cs`: app state, hotkey handling, event logging
- `Assets/keybridge.ico`: Windows app icon
- `tools/Create-AppIcon.ps1`: regenerates icon assets from the downloaded source image
- `Models/DeviceProfile.cs`: internal BLE peer label
- `Models/ActivityEvent.cs`: event log item
- `Services/IHidBridgeService.cs`: backend contract for future real HID transport
- `Services/BluetoothCapabilityProbe.cs`: Windows BLE/HID capability probe
- `Services/BluetoothHidBridgeService.cs`: experimental BLE HID keyboard backend
- `Services/HidKeyboardReport.cs`: WPF key to HID keyboard report mapper
- `Services/GlobalInputHookService.cs`: low-level global keyboard/mouse capture
- `Services/SimulatedHidBridgeService.cs`: current no-op backend

## Next backend milestone

Replace `SimulatedHidBridgeService` with a real implementation while keeping the UI contract unchanged:

```csharp
public sealed class BluetoothHidBridgeService : IHidBridgeService
{
    // Start advertising/pairing as a keyboard/mouse HID endpoint.
    // Translate WPF key/pointer events into HID reports.
}
```
