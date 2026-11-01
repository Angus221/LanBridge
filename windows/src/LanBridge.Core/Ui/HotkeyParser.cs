namespace LanBridge.Core;

public static class HotkeyParser
{
    public const int ModAlt = 0x0001;
    public const int ModControl = 0x0002;
    public const int ModShift = 0x0004;
    public const int ModWin = 0x0008;
    public const int ModNoRepeat = 0x4000;

    public const string DefaultSend = "Ctrl+Alt+V";
    public const string DefaultSidebar = "Ctrl+Alt+L";

    public sealed record Parsed(int Modifiers, int VirtualKey, string Canonical);

    public static bool TryParse(string? text, out Parsed parsed, out string? error)
    {
        parsed = new Parsed(0, 0, "");
        error = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            error = "快捷键不能为空。";
            return false;
        }

        var parts = text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var mods = 0;
        var vk = 0;
        var seenKey = false;
        foreach (var raw in parts)
        {
            var token = raw.Trim();
            if (Is(token, "ctrl", "control", "ctl")) { mods |= ModControl; continue; }
            if (Is(token, "alt")) { mods |= ModAlt; continue; }
            if (Is(token, "shift")) { mods |= ModShift; continue; }
            if (Is(token, "win", "windows", "meta")) { mods |= ModWin; continue; }
            if (seenKey)
            {
                error = "快捷键只能有一个主键。";
                return false;
            }
            if (!TryKey(token, out vk))
            {
                error = "无法识别的按键：" + token;
                return false;
            }
            seenKey = true;
        }

        if (!seenKey)
        {
            error = "还缺少主键，例如 V 或 L。";
            return false;
        }
        if ((mods & (ModControl | ModAlt | ModWin)) == 0)
        {
            error = "请至少带上 Ctrl、Alt 或 Win，避免占用单独的字母键。";
            return false;
        }

        parsed = new Parsed(mods | ModNoRepeat, vk, Canonical(mods, vk));
        return true;
    }

    public static string? Conflict(string? left, string? right)
    {
        if (!TryParse(left, out var a, out var errorA)) return errorA;
        if (!TryParse(right, out var b, out var errorB)) return errorB;
        if (a.VirtualKey == b.VirtualKey && (a.Modifiers & 0xF) == (b.Modifiers & 0xF))
            return "两个快捷键不能相同。";
        return null;
    }

    public static string Canonical(int modifiers, int virtualKey)
    {
        var parts = new List<string>();
        if ((modifiers & ModControl) != 0) parts.Add("Ctrl");
        if ((modifiers & ModAlt) != 0) parts.Add("Alt");
        if ((modifiers & ModShift) != 0) parts.Add("Shift");
        if ((modifiers & ModWin) != 0) parts.Add("Win");
        parts.Add(KeyName(virtualKey));
        return string.Join("+", parts);
    }

    private static bool Is(string token, params string[] names)
        => names.Any(name => token.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static bool TryKey(string token, out int vk)
    {
        vk = 0;
        if (token.Length == 1)
        {
            var ch = char.ToUpperInvariant(token[0]);
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9')
            {
                vk = ch;
                return true;
            }
        }
        if (token.Length is >= 2 and <= 3 && (token[0] == 'F' || token[0] == 'f') && int.TryParse(token[1..], out var fn) && fn is >= 1 and <= 12)
        {
            vk = 0x70 + fn - 1;
            return true;
        }
        return false;
    }

    private static string KeyName(int vk) => vk switch
    {
        >= 'A' and <= 'Z' => ((char)vk).ToString(),
        >= '0' and <= '9' => ((char)vk).ToString(),
        >= 0x70 and <= 0x7B => "F" + (vk - 0x70 + 1),
        _ => "Key" + vk
    };
}
