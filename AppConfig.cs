using System.Text.Json;

namespace ZoneLab;

internal sealed class AppConfig
{
    public string CaptureKey { get; set; } = "Q";
    public string DiscardKey { get; set; } = "M";
    public List<ZoneConfig> Zones { get; set; } = CreateDefaultZones();
    public int CropSize { get; set; } = 96;
    public bool AlwaysOnTop { get; set; }
    public bool HideWhileCapturing { get; set; }

    public static AppConfig CreateDefault() => new();

    public AppConfig Copy() => new()
    {
        CaptureKey = CaptureKey,
        DiscardKey = DiscardKey,
        Zones = Zones.Select(zone => new ZoneConfig { Name = zone.Name, Key = zone.Key }).ToList(),
        CropSize = CropSize,
        AlwaysOnTop = AlwaysOnTop,
        HideWhileCapturing = HideWhileCapturing,
    };

    public static AppConfig Load(string path)
    {
        if (!File.Exists(path))
            return CreateDefault();

        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), options)
            ?? throw new InvalidDataException("config.json is empty or invalid.");

        Validate(config);
        return config;
    }

    public static void Validate(AppConfig config)
    {
        if (config.Zones is null || config.Zones.Count is < 1 or > 20)
            throw new InvalidDataException("Configure between 1 and 20 zones.");
        if (config.CropSize is < 48 or > 192)
            throw new InvalidDataException("Crop size must be between 48 and 192 pixels.");

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

        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string value in new[] { config.CaptureKey, config.DiscardKey }.Concat(config.Zones.Select(zone => zone.Key)))
        {
            string key = value?.Trim() ?? string.Empty;
            if (key.Length == 0) continue;
            Keys parsed = ParseConfiguredKey(key);
            if (!keys.Add(parsed.ToString()))
                throw new InvalidDataException($"The shortcut '{FormatKey(parsed)}' is assigned more than once.");
        }

        config.CaptureKey = NormalizeKey(config.CaptureKey);
        config.DiscardKey = NormalizeKey(config.DiscardKey);
        foreach (var zone in config.Zones)
            zone.Key = NormalizeKey(zone.Key);
    }

    public static bool IsSupportedKey(Keys key) =>
        key is >= Keys.D0 and <= Keys.D9
        or >= Keys.NumPad0 and <= Keys.NumPad9
        or >= Keys.A and <= Keys.Z
        or >= Keys.F1 and <= Keys.F12;

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
        return ToConfigKey(ParseConfiguredKey(value));
    }

    public static Keys ParseKey(string? value) =>
        string.IsNullOrWhiteSpace(value) ? Keys.None : ParseConfiguredKey(value);

    public static string ToConfigKey(Keys key)
    {
        if (key is >= Keys.D0 and <= Keys.D9)
            return ((int)key - (int)Keys.D0).ToString();
        if (key is >= Keys.NumPad0 and <= Keys.NumPad9)
            return $"NumPad{(int)key - (int)Keys.NumPad0}";
        return key == Keys.None ? string.Empty : key.ToString();
    }

    private static Keys ParseConfiguredKey(string value)
    {
        string keyText = value.Trim();
        if (keyText.Length == 1 && char.IsDigit(keyText[0]))
            return (Keys)((int)Keys.D0 + (keyText[0] - '0'));
        if (keyText.StartsWith("Num ", StringComparison.OrdinalIgnoreCase)
            && keyText.Length == 5 && char.IsDigit(keyText[4]))
            return (Keys)((int)Keys.NumPad0 + (keyText[4] - '0'));
        if (keyText.StartsWith("NumPad", StringComparison.OrdinalIgnoreCase)
            && keyText.Length == 7 && char.IsDigit(keyText[6]))
            return (Keys)((int)Keys.NumPad0 + (keyText[6] - '0'));
        if (Enum.TryParse(keyText, ignoreCase: true, out Keys key) && IsSupportedKey(key))
            return key;
        throw new InvalidDataException($"'{value}' is not a supported shortcut key.");
    }

    private static List<ZoneConfig> CreateDefaultZones() =>
    [
        new() { Name = "head", Key = "1" },
        new() { Name = "chest", Key = "2" },
        new() { Name = "body", Key = "3" },
        new() { Name = "legs", Key = "4" },
        new() { Name = "none", Key = "5" },
    ];
}

internal sealed class ZoneConfig
{
    public string Name { get; set; } = string.Empty;
    public string Key { get; set; } = string.Empty;
}
