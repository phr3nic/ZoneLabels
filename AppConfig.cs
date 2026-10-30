using System.Text.Json;
using System.Runtime.InteropServices;

namespace ZoneLab;

internal sealed class AppConfig
{
    public string CaptureKey { get; set; } = "Alt+Q";
    public string DiscardKey { get; set; } = "M";
    public string PrecisionKey { get; set; } = "LeftShift";
    public float PrecisionFactor { get; set; } = 0.25f;
    public List<ZoneConfig> Zones { get; set; } = CreateDefaultZones();
    public bool AlwaysOnTop { get; set; }
    public bool HideWhileCapturing { get; set; }
    public int DoneRetentionDays { get; set; } = 7;
    public Dictionary<string, int> TargetCounts { get; set; } = CreateDefaultTargetCounts();
    public int MinPinSpacing { get; set; } = 40;
    public int? WindowX { get; set; }
    public int? WindowY { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool? SidebarCollapsed { get; set; }

    public static AppConfig CreateDefault() => new();

    public AppConfig Copy() => new()
    {
        CaptureKey = CaptureKey,
        DiscardKey = DiscardKey,
        PrecisionKey = PrecisionKey,
        PrecisionFactor = PrecisionFactor,
        Zones = Zones.Select(zone => new ZoneConfig { Name = zone.Name, Key = zone.Key }).ToList(),
        AlwaysOnTop = AlwaysOnTop,
        HideWhileCapturing = HideWhileCapturing,
        DoneRetentionDays = DoneRetentionDays,
        TargetCounts = new Dictionary<string, int>(TargetCounts, StringComparer.OrdinalIgnoreCase),
        MinPinSpacing = MinPinSpacing,
        WindowX = WindowX,
        WindowY = WindowY,
        WindowWidth = WindowWidth,
        WindowHeight = WindowHeight,
        SidebarCollapsed = SidebarCollapsed,
    };

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return CreateDefault();

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("config.json is empty or invalid.");

        if (config.TargetCounts is not null)
            config.TargetCounts = new Dictionary<string, int>(config.TargetCounts, StringComparer.OrdinalIgnoreCase);
        MigratePreviousDefaults(config);
        Validate(config);
        return config;
    }

    public static void Validate(AppConfig config)
    {
        if (config.Zones is null || config.Zones.Count is < 1 or > 20)
            throw new InvalidDataException("Configure between 1 and 20 zones.");
        if (config.DoneRetentionDays is < 0 or > 3650)
            throw new InvalidDataException("Done retention must be between 0 and 3650 days.");
        if (config.MinPinSpacing is < 0 or > 10000)
            throw new InvalidDataException("Minimum pin spacing must be between 0 and 10000 pixels.");
        if (ParsePrecisionKey(config.PrecisionKey) == Keys.None)
            throw new InvalidDataException("PrecisionKey must be LeftShift, RightShift, LeftControl, RightControl, LeftAlt, or RightAlt.");
        if (!float.IsFinite(config.PrecisionFactor) || config.PrecisionFactor is < 0.05f or > 1f)
            throw new InvalidDataException("PrecisionFactor must be between 0.05 and 1.");
        if (config.WindowWidth is < 1 or > 10000 || config.WindowHeight is < 1 or > 10000)
            throw new InvalidDataException("Saved window dimensions must be between 1 and 10000 pixels.");
        if (config.WindowX is < -100000 or > 100000 || config.WindowY is < -100000 or > 100000)
            throw new InvalidDataException("Saved window position is outside the supported coordinate range.");
        if (config.TargetCounts is null)
            throw new InvalidDataException("Target counts cannot be null.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var zone in config.Zones)
        {
            if (zone is null)
                throw new InvalidDataException("A zone entry is missing.");
            zone.Name = zone.Name?.Trim() ?? string.Empty;
            if (zone.Name.Length is < 1 or > 40
                || zone.Name.Any(character => !char.IsLetterOrDigit(character)
                    && character is not ' ' and not '_' and not '-'))
                throw new InvalidDataException("Zone names must be 1-40 characters and contain only letters, numbers, spaces, '-' or '_'.");
            if (!names.Add(zone.Name))
                throw new InvalidDataException($"The zone '{zone.Name}' is listed more than once.");
        }

        foreach (ZoneConfig zone in config.Zones)
        {
            if (!config.TargetCounts.ContainsKey(zone.Name))
                config.TargetCounts[zone.Name] = 500;
        }

        foreach ((string name, int count) in config.TargetCounts)
        {
            if (string.IsNullOrWhiteSpace(name) || count < 0)
                throw new InvalidDataException("Target count names must be non-empty and counts cannot be negative.");
        }

        config.CaptureKey = NormalizeCaptureKey(config.CaptureKey);
        HotkeyBinding captureKey = ParseHotkey(config.CaptureKey);
        if (captureKey.Key != Keys.None && !IsSupportedCaptureKey(captureKey.Key))
            throw new InvalidDataException("The capture key must be a letter or digit.");
        var keys = new HashSet<HotkeyBinding>();
        foreach (string value in new[] { config.CaptureKey, config.DiscardKey }.Concat(config.Zones.Select(zone => zone.Key)))
        {
            string key = value?.Trim() ?? string.Empty;
            if (key.Length == 0) continue;
            HotkeyBinding parsed = ParseHotkey(key);
            if (parsed == new HotkeyBinding(Keys.Z, HotkeyModifiers.Control))
                throw new InvalidDataException("Ctrl+Z is reserved for undo.");
            if (!keys.Add(parsed))
                throw new InvalidDataException($"The shortcut '{FormatHotkey(parsed)}' is assigned more than once.");
        }

        config.DiscardKey = NormalizeKey(config.DiscardKey);
        foreach (var zone in config.Zones)
            zone.Key = NormalizeKey(zone.Key);
    }

    public static Keys ParsePrecisionKey(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "leftshift" => Keys.LShiftKey,
        "rightshift" => Keys.RShiftKey,
        "leftcontrol" => Keys.LControlKey,
        "rightcontrol" => Keys.RControlKey,
        "leftalt" => Keys.LMenu,
        "rightalt" => Keys.RMenu,
        _ => Keys.None,
    };

    public static bool IsSupportedKey(Keys key)
    {
        key &= Keys.KeyCode;
        return key != Keys.None
            && Enum.IsDefined(key)
            && key is not Keys.ShiftKey and not Keys.LShiftKey and not Keys.RShiftKey
                and not Keys.ControlKey and not Keys.LControlKey and not Keys.RControlKey
                and not Keys.Menu and not Keys.LMenu and not Keys.RMenu
                and not Keys.LWin and not Keys.RWin;
    }

    public static string FormatKey(Keys key) => key switch
    {
        Keys.None => "None",
        >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => $"Num {(int)key - (int)Keys.NumPad0}",
        _ => key.ToString(),
    };

    public static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        return ToConfigKey(ParseHotkey(value));
    }

    public static string NormalizeCaptureKey(string? value)
    {
        return NormalizeKey(value);
    }

    public static bool IsSupportedCaptureKey(Keys key)
    {
        key &= Keys.KeyCode;
        return key is >= Keys.A and <= Keys.Z
            or >= Keys.D0 and <= Keys.D9;
    }

    public static HotkeyBinding ParseHotkey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? new HotkeyBinding(Keys.None, HotkeyModifiers.None) : ParseConfiguredHotkey(value);

    public static string ToConfigKey(HotkeyBinding binding)
    {
        if (binding.Key == Keys.None)
            return string.Empty;
        var parts = new List<string>();
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (binding.Modifiers.HasFlag(HotkeyModifiers.Windows)) parts.Add("Win");
        parts.Add(KeyToConfigKey(binding.Key));
        return string.Join("+", parts);
    }

    public static string FormatHotkey(HotkeyBinding binding)
    {
        if (binding.Key == Keys.None)
            return "None";
        return ToConfigKey(binding).Replace("NumPad", "Num ", StringComparison.Ordinal);
    }

    private static string KeyToConfigKey(Keys key)
    {
        if (key is >= Keys.D0 and <= Keys.D9)
            return ((int)key - (int)Keys.D0).ToString();
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
            return $"NumPad{(int)key - (int)Keys.NumPad0}";
        return key == Keys.None ? string.Empty : key.ToString();
    }

    private static HotkeyBinding ParseConfiguredHotkey(string value)
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        string? keyText = null;
        foreach (string rawPart in value.Split('+'))
        {
            string part = rawPart.Trim();
            if (part.Length == 0)
                throw new InvalidDataException($"'{value}' is not a supported shortcut.");
            switch (part.ToLowerInvariant())
            {
                case "ctrl":
                case "control":
                    modifiers |= HotkeyModifiers.Control;
                    break;
                case "alt":
                    modifiers |= HotkeyModifiers.Alt;
                    break;
                case "shift":
                    modifiers |= HotkeyModifiers.Shift;
                    break;
                case "win":
                case "windows":
                    modifiers |= HotkeyModifiers.Windows;
                    break;
                default:
                    if (keyText is not null || !TryParseKey(part, out Keys parsedKey))
                        throw new InvalidDataException($"'{value}' is not a supported shortcut.");
                    keyText = part;
                    break;
            }
        }

        if (keyText is null || !TryParseKey(keyText, out Keys key) || !IsSupportedKey(key))
            throw new InvalidDataException($"'{value}' is not a supported shortcut key.");
        return new HotkeyBinding(key, modifiers);
    }

    private static bool TryParseKey(string keyText, out Keys key)
    {
        key = Keys.None;
        if (keyText.Length == 1 && char.IsDigit(keyText[0]))
        {
            key = (Keys)((int)Keys.D0 + (keyText[0] - '0'));
            return true;
        }
        if (keyText.StartsWith("Num ", StringComparison.OrdinalIgnoreCase)
            && keyText.Length == 5 && char.IsDigit(keyText[4]))
        {
            key = (Keys)((int)Keys.NumPad0 + (keyText[4] - '0'));
            return true;
        }
        if (keyText.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && keyText.Length == 7 && char.IsDigit(keyText[6]))
        {
            key = (Keys)((int)Keys.NumPad0 + (keyText[6] - '0'));
            return true;
        }
        return Enum.TryParse(keyText, ignoreCase: true, out key) && IsSupportedKey(key);
    }

    private static List<ZoneConfig> CreateDefaultZones() =>
    [
        new() { Name = "head", Key = "1" },
        new() { Name = "chest", Key = "2" },
        new() { Name = "body", Key = "3" },
        new() { Name = "arms", Key = "4" },
        new() { Name = "legs", Key = "5" },
        new() { Name = "none", Key = "6" },
    ];

    private static Dictionary<string, int> CreateDefaultTargetCounts() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["head"] = 500,
            ["chest"] = 500,
            ["body"] = 500,
            ["arms"] = 500,
            ["legs"] = 500,
            ["none"] = 500,
        };

    private static void MigratePreviousDefaults(AppConfig config)
    {
        HotkeyBinding previousCapture = ParseHotkey(config.CaptureKey);
        if (previousCapture.Key != Keys.None && !IsSupportedCaptureKey(previousCapture.Key))
            config.CaptureKey = "Alt+Q";

        var oldZones = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["head"] = "1",
            ["chest"] = "2",
            ["body"] = "3",
            ["legs"] = "4",
            ["none"] = "5",
        };
        if (config.Zones is not null
            && config.Zones.Count == oldZones.Count
            && config.Zones.All(zone => zone is not null
                && !string.IsNullOrEmpty(zone.Name)
                && oldZones.TryGetValue(zone.Name, out string? key)
                && string.Equals(zone.Key, key, StringComparison.OrdinalIgnoreCase)))
            config.Zones = CreateDefaultZones();

        var oldTargets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["head"] = 500,
            ["chest"] = 300,
            ["body"] = 300,
            ["legs"] = 300,
            ["none"] = 500,
        };
        if (config.TargetCounts is not null
            && config.TargetCounts.Count == oldTargets.Count
            && oldTargets.All(pair => config.TargetCounts.TryGetValue(pair.Key, out int value)
                && value == pair.Value))
            config.TargetCounts = CreateDefaultTargetCounts();
    }
}

internal sealed class ZoneConfig
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}

[Flags]
internal enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Windows = 8,
}

internal readonly record struct HotkeyBinding(Keys Key, HotkeyModifiers Modifiers)
{
    [DllImport("user32.dll")]
    private static extern short GetKeyState(int virtualKey);

    public static HotkeyModifiers ReadModifiers(Keys modifiers)
    {
        HotkeyModifiers result = HotkeyModifiers.None;
        if ((modifiers & Keys.Control) != 0) result |= HotkeyModifiers.Control;
        if ((modifiers & Keys.Alt) != 0) result |= HotkeyModifiers.Alt;
        if ((modifiers & Keys.Shift) != 0) result |= HotkeyModifiers.Shift;
        if ((GetKeyState(0x5B) & 0x8000) != 0 || (GetKeyState(0x5C) & 0x8000) != 0)
            result |= HotkeyModifiers.Windows;
        return result;
    }

    public bool Matches(Keys key, Keys modifiers) =>
        Key == key && Modifiers == ReadModifiers(modifiers);
}
