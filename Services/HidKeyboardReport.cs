using System.Windows.Input;

namespace KeyboardPadBridge.Services;

public static class HidKeyboardReport
{
    private const byte LeftControl = 0x01;
    private const byte LeftShift = 0x02;
    private const byte LeftAlt = 0x04;
    private const byte LeftGui = 0x08;
    private const byte RightControl = 0x10;
    private const byte RightShift = 0x20;
    private const byte RightAlt = 0x40;
    private const byte RightGui = 0x80;

    public static byte[] Empty { get; } = new byte[8];

    public static byte[] FromKey(Key key)
    {
        return FromPressedKeys(new[] { new CapturedKey(key, KeyInterop.VirtualKeyFromKey(key)) });
    }

    public static byte[] FromPressedKeys(IEnumerable<CapturedKey> pressedKeys)
    {
        var keys = pressedKeys.ToList();
        var report = new byte[8];

        if (keys.Count == 0)
        {
            return report;
        }

        if (TryCreateAltTabAppSwitcher(keys, report))
        {
            return report;
        }

        if (TryCreateKeyBridgeShortcut(keys, report))
        {
            return report;
        }

        if (ContainsHangulToggle(keys))
        {
            report[0] = LeftControl;
            report[2] = 0x2C; // Ctrl+Space toggles hardware keyboard language on iPadOS.
            return report;
        }

        var modifier = keys.Aggregate((byte)0, (current, key) => (byte)(current | GetModifier(key)));
        var usages = keys
            .Select(GetUsage)
            .Where(usage => usage != 0)
            .Distinct()
            .Take(6)
            .ToList();

        if (ShouldTranslateControlShortcutToCommand(keys, usages))
        {
            modifier = (byte)((modifier & ~(LeftControl | RightControl)) | LeftGui);
        }

        report[0] = modifier;

        for (var index = 0; index < usages.Count; index++)
        {
            report[index + 2] = usages[index];
        }

        return report;
    }

    public static string DescribePressedKeys(IEnumerable<CapturedKey> pressedKeys)
    {
        var keyNames = pressedKeys
            .Select(key => key.Key.ToString())
            .OrderBy(name => name)
            .ToList();

        return keyNames.Count == 0 ? "none" : string.Join("+", keyNames);
    }

    private static bool TryCreateAltTabAppSwitcher(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasAlt = keys.Any(key => (GetModifier(key) & (LeftAlt | RightAlt)) != 0);
        var hasControl = keys.Any(key => (GetModifier(key) & (LeftControl | RightControl)) != 0);

        if (!hasAlt || hasControl)
        {
            return false;
        }

        var hasTab = keys.Any(key => GetUsage(key) == 0x2B);
        var hasShift = keys.Any(key => (GetModifier(key) & (LeftShift | RightShift)) != 0);

        report[0] = (byte)(LeftGui | (hasShift ? LeftShift : 0x00));

        if (hasTab)
        {
            report[2] = 0x2B;
        }

        return hasTab || keys.All(key => GetUsage(key) == 0);
    }

    private static bool TryCreateKeyBridgeShortcut(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasControl = keys.Any(key => (GetModifier(key) & (LeftControl | RightControl)) != 0);
        var hasAlt = keys.Any(key => (GetModifier(key) & (LeftAlt | RightAlt)) != 0);

        if (!hasControl || !hasAlt)
        {
            return false;
        }

        var requestedUsage = keys
            .Select(GetUsage)
            .FirstOrDefault(usage => usage is 0x0F or 0x2C); // L or Space

        if (requestedUsage == 0)
        {
            return false;
        }

        report[0] = LeftGui;
        report[2] = requestedUsage;
        return true;
    }

    private static bool ContainsHangulToggle(IReadOnlyCollection<CapturedKey> keys)
    {
        return keys.Count == 1 && keys.Any(IsHangulHardwareKey);
    }

    private static bool IsHangulHardwareKey(CapturedKey key)
    {
        return key.VirtualKey is 0x15 or 0xA5
            || key.Key is Key.HangulMode or Key.KanaMode or Key.RightAlt;
    }

    private static bool ShouldTranslateControlShortcutToCommand(
        IReadOnlyCollection<CapturedKey> keys,
        IReadOnlyCollection<byte> usages)
    {
        var hasControl = keys.Any(key => (GetModifier(key) & (LeftControl | RightControl)) != 0);
        var hasAlt = keys.Any(key => (GetModifier(key) & (LeftAlt | RightAlt)) != 0);

        if (!hasControl || hasAlt)
        {
            return false;
        }

        byte[] windowsStyleShortcutUsages =
        {
            0x04, // A: select all
            0x05, // B: bold
            0x06, // C: copy
            0x09, // F: find
            0x0C, // I: italic
            0x0F, // L: focus address/search field
            0x11, // N: new
            0x13, // P: print
            0x15, // R: refresh/reload
            0x16, // S: save
            0x17, // T: new tab
            0x18, // U: underline
            0x19, // V: paste
            0x1A, // W: close tab/window
            0x1B, // X: cut
            0x1C, // Y: redo
            0x1D, // Z: undo
        };

        return usages.Any(windowsStyleShortcutUsages.Contains);
    }

    private static byte GetModifier(CapturedKey capturedKey)
    {
        return capturedKey.VirtualKey switch
        {
            0xA0 => LeftShift,
            0xA1 => RightShift,
            0xA2 => LeftControl,
            0xA3 => RightControl,
            0xA4 => LeftAlt,
            0xA5 => 0x00,
            0x5B => 0x00,
            0x5C => 0x00,
            _ => capturedKey.Key switch
            {
                Key.LeftCtrl => LeftControl,
                Key.RightCtrl => RightControl,
                Key.LeftShift => LeftShift,
                Key.RightShift => RightShift,
                Key.LeftAlt => LeftAlt,
                Key.RightAlt => 0x00,
                Key.LWin => 0x00,
                Key.RWin => 0x00,
                _ => 0x00
            }
        };
    }

    private static byte GetUsage(CapturedKey capturedKey)
    {
        if (capturedKey.VirtualKey is >= 0x31 and <= 0x39)
        {
            return (byte)(0x1E + capturedKey.VirtualKey - 0x31);
        }

        if (capturedKey.VirtualKey == 0x30)
        {
            return 0x27;
        }

        if (capturedKey.VirtualKey is >= 0x61 and <= 0x69)
        {
            return (byte)(0x59 + capturedKey.VirtualKey - 0x61);
        }

        if (capturedKey.VirtualKey == 0x60)
        {
            return 0x62;
        }

        var key = capturedKey.Key == Key.System ? KeyInterop.KeyFromVirtualKey(capturedKey.VirtualKey) : capturedKey.Key;

        if (key is >= Key.A and <= Key.Z)
        {
            return (byte)(0x04 + key - Key.A);
        }

        if (key is >= Key.D1 and <= Key.D9)
        {
            return (byte)(0x1E + key - Key.D1);
        }

        if (key == Key.D0)
        {
            return 0x27;
        }

        if (key is >= Key.F1 and <= Key.F12)
        {
            return (byte)(0x3A + key - Key.F1);
        }

        if (key is >= Key.NumPad1 and <= Key.NumPad9)
        {
            return (byte)(0x59 + key - Key.NumPad1);
        }

        if (key == Key.NumPad0)
        {
            return 0x62;
        }

        return key switch
        {
            Key.Enter => 0x28,
            Key.Escape => 0x29,
            Key.Back => 0x2A,
            Key.Tab => 0x2B,
            Key.Space => 0x2C,
            Key.OemMinus => 0x2D,
            Key.OemPlus => 0x2E,
            Key.OemOpenBrackets => 0x2F,
            Key.OemCloseBrackets => 0x30,
            Key.OemPipe => 0x31,
            Key.OemSemicolon => 0x33,
            Key.OemQuotes => 0x34,
            Key.OemTilde => 0x35,
            Key.OemComma => 0x36,
            Key.OemPeriod => 0x37,
            Key.OemQuestion => 0x38,
            Key.CapsLock => 0x39,
            Key.PrintScreen => 0x46,
            Key.Scroll => 0x47,
            Key.Pause => 0x48,
            Key.Insert => 0x49,
            Key.Home => 0x4A,
            Key.PageUp => 0x4B,
            Key.Delete => 0x4C,
            Key.End => 0x4D,
            Key.PageDown => 0x4E,
            Key.Right => 0x4F,
            Key.Left => 0x50,
            Key.Down => 0x51,
            Key.Up => 0x52,
            Key.NumLock => 0x53,
            Key.Divide => 0x54,
            Key.Multiply => 0x55,
            Key.Subtract => 0x56,
            Key.Add => 0x57,
            Key.Separator => 0x58,
            Key.Decimal => 0x63,
            Key.Apps => 0x65,
            _ => 0x00
        };
    }
}
