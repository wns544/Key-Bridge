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

        if (TryCreateLineNavigation(keys, report))
        {
            return report;
        }

        if (TryCreateWordNavigation(keys, report))
        {
            return report;
        }

        if (TryCreateWordDeletion(keys, report))
        {
            return report;
        }

        if (TryCreateIpadFunctionShortcut(keys, report))
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
            .Select(key => GetUsage(key, modifier))
            .Where(usage => usage != 0)
            .Distinct()
            .Take(6)
            .ToList();

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

    public static bool TryCreateTextInputReport(char character, out byte[] report)
    {
        report = new byte[8];

        if (character is >= 'a' and <= 'z')
        {
            report[2] = (byte)(0x04 + character - 'a');
            return true;
        }

        if (character is >= 'A' and <= 'Z')
        {
            report[0] = LeftShift;
            report[2] = (byte)(0x04 + character - 'A');
            return true;
        }

        if (character is >= '1' and <= '9')
        {
            report[2] = (byte)(0x1E + character - '1');
            return true;
        }

        if (character == '0')
        {
            report[2] = 0x27;
            return true;
        }

        return character switch
        {
            '\n' => SetTextReport(report, 0x00, 0x28),
            '\t' => SetTextReport(report, 0x00, 0x2B),
            ' ' => SetTextReport(report, 0x00, 0x2C),
            '-' => SetTextReport(report, 0x00, 0x2D),
            '_' => SetTextReport(report, LeftShift, 0x2D),
            '=' => SetTextReport(report, 0x00, 0x2E),
            '+' => SetTextReport(report, LeftShift, 0x2E),
            '[' => SetTextReport(report, 0x00, 0x2F),
            '{' => SetTextReport(report, LeftShift, 0x2F),
            ']' => SetTextReport(report, 0x00, 0x30),
            '}' => SetTextReport(report, LeftShift, 0x30),
            '\\' => SetTextReport(report, 0x00, 0x31),
            '|' => SetTextReport(report, LeftShift, 0x31),
            ';' => SetTextReport(report, 0x00, 0x33),
            ':' => SetTextReport(report, LeftShift, 0x33),
            '\'' => SetTextReport(report, 0x00, 0x34),
            '"' => SetTextReport(report, LeftShift, 0x34),
            '`' => SetTextReport(report, 0x00, 0x35),
            '~' => SetTextReport(report, LeftShift, 0x35),
            ',' => SetTextReport(report, 0x00, 0x36),
            '<' => SetTextReport(report, LeftShift, 0x36),
            '.' => SetTextReport(report, 0x00, 0x37),
            '>' => SetTextReport(report, LeftShift, 0x37),
            '/' => SetTextReport(report, 0x00, 0x38),
            '?' => SetTextReport(report, LeftShift, 0x38),
            '!' => SetTextReport(report, LeftShift, 0x1E),
            '@' => SetTextReport(report, LeftShift, 0x1F),
            '#' => SetTextReport(report, LeftShift, 0x20),
            '$' => SetTextReport(report, LeftShift, 0x21),
            '%' => SetTextReport(report, LeftShift, 0x22),
            '^' => SetTextReport(report, LeftShift, 0x23),
            '&' => SetTextReport(report, LeftShift, 0x24),
            '*' => SetTextReport(report, LeftShift, 0x25),
            '(' => SetTextReport(report, LeftShift, 0x26),
            ')' => SetTextReport(report, LeftShift, 0x27),
            _ => false
        };
    }

    private static bool SetTextReport(byte[] report, byte modifier, byte usage)
    {
        report[0] = modifier;
        report[2] = usage;
        return true;
    }

    private static bool TryCreateAltTabAppSwitcher(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasAlt = keys.Any(key => (GetModifier(key) & (LeftAlt | RightAlt)) != 0);
        var hasControl = keys.Any(IsControlKey);

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

    private static bool TryCreateLineNavigation(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasHome = keys.Any(key => GetUsage(key) == 0x4A);
        var hasEnd = keys.Any(key => GetUsage(key) == 0x4D);

        if (!hasHome && !hasEnd)
        {
            return false;
        }

        var hasShift = keys.Any(key => (GetModifier(key) & (LeftShift | RightShift)) != 0);
        report[0] = (byte)(LeftGui | (hasShift ? LeftShift : 0x00));
        report[2] = hasHome ? (byte)0x50 : (byte)0x4F; // iPadOS line start/end: Command+Left/Right.
        return true;
    }

    private static bool TryCreateWordNavigation(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasControl = keys.Any(IsControlKey);
        var hasLeft = keys.Any(key => GetUsage(key) == 0x50);
        var hasRight = keys.Any(key => GetUsage(key) == 0x4F);

        if (!hasControl || (!hasLeft && !hasRight))
        {
            return false;
        }

        var hasShift = keys.Any(key => (GetModifier(key) & (LeftShift | RightShift)) != 0);
        report[0] = (byte)(LeftAlt | (hasShift ? LeftShift : 0x00));
        report[2] = hasLeft ? (byte)0x50 : (byte)0x4F; // iPadOS word navigation: Option+Left/Right.
        return true;
    }

    private static bool TryCreateWordDeletion(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasControl = keys.Any(IsControlKey);
        var hasBackspace = keys.Any(key => GetUsage(key) == 0x2A);
        var hasDelete = keys.Any(key => GetUsage(key) == 0x4C);

        if (!hasControl || (!hasBackspace && !hasDelete))
        {
            return false;
        }

        report[0] = LeftAlt;
        report[2] = hasBackspace ? (byte)0x2A : (byte)0x4C; // iPadOS word deletion: Option+Backspace/Delete.
        return true;
    }

    private static bool TryCreateIpadFunctionShortcut(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasF1 = keys.Any(key => GetUsage(key) == 0x3A);
        var hasF5 = keys.Any(key => GetUsage(key) == 0x3E);
        var hasF9 = keys.Any(key => GetUsage(key) == 0x42);
        var hasF10 = keys.Any(key => GetUsage(key) == 0x43);
        var hasF11 = keys.Any(key => GetUsage(key) == 0x44);

        if (!hasF1 && !hasF5 && !hasF9 && !hasF10 && !hasF11)
        {
            return false;
        }

        if (hasF1)
        {
            report[0] = LeftGui;
            report[2] = 0x0B; // Command+H: go to Home screen.
            return true;
        }

        if (hasF5)
        {
            report[0] = LeftGui;
            report[2] = 0x15; // Command+R: refresh current page/window on iPadOS.
            return true;
        }

        if (hasF9)
        {
            report[0] = LeftGui;
            report[2] = 0x52; // Command+Up: top of document/page.
            return true;
        }

        if (hasF10)
        {
            report[0] = LeftGui;
            report[2] = 0x51; // Command+Down: bottom of document/page.
            return true;
        }

        if (hasF11)
        {
            report[0] = LeftGui | LeftAlt;
            report[2] = 0x07; // Command+Option+D: show/hide Dock.
            return true;
        }

        return false;
    }

    private static bool TryCreateKeyBridgeShortcut(IReadOnlyCollection<CapturedKey> keys, byte[] report)
    {
        var hasControl = keys.Any(IsControlKey);
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

    private static bool IsControlKey(CapturedKey key)
    {
        return key.VirtualKey is 0x11 or 0xA2 or 0xA3
            || key.Key is Key.LeftCtrl or Key.RightCtrl;
    }

    private static byte GetModifier(CapturedKey capturedKey)
    {
        return capturedKey.VirtualKey switch
        {
            0xA0 => LeftShift,
            0xA1 => RightShift,
            0xA2 => LeftGui, // Map Left Control to iPad Command
            0xA3 => LeftGui, // Map Right Control to iPad Command
            0xA4 => LeftAlt,
            0xA5 => 0x00,
            0x5B => 0x00,
            0x5C => 0x00,
            _ => capturedKey.Key switch
            {
                Key.LeftCtrl => LeftGui, // Map Left Control to iPad Command
                Key.RightCtrl => LeftGui, // Map Right Control to iPad Command
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
        return GetUsage(capturedKey, 0);
    }

    private static byte GetUsage(CapturedKey capturedKey, byte modifier)
    {
        if ((modifier & (LeftControl | RightControl | LeftAlt | RightAlt | LeftGui | RightGui | LeftShift | RightShift)) == 0
            && capturedKey.VirtualKey is >= 0x31 and <= 0x39)
        {
            return (byte)(0x59 + capturedKey.VirtualKey - 0x31);
        }

        if ((modifier & (LeftControl | RightControl | LeftAlt | RightAlt | LeftGui | RightGui | LeftShift | RightShift)) == 0
            && capturedKey.VirtualKey == 0x30)
        {
            return 0x62;
        }

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
