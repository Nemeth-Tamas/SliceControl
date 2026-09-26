using System.Text.Json;

namespace SliceTranscribe;

internal static class RadioPresetStore
{
    private static readonly object Gate =
        new();

    private static readonly string PresetPath =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SliceAppliance",
            "radio-presets.json");

    private static readonly Dictionary<string, string> DisplayNames =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["retro"] = "Retro Rádió",
            ["retro-backup"] = "Retro Rádió (backup)",
            ["radio1"] = "Rádió 1",
            ["juventus"] = "Juventus Rádió",
            ["jazzy"] = "Jazzy Rádió 90.9"
        };

    private static readonly Dictionary<string, string> Defaults =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["retro"] =
                "https://icast.connectmedia.hu/5002/live.mp3",

            ["retro-backup"] =
                "https://icast.connectmedia.hu/5001/live.mp3",

            ["radio1"] =
                "https://icast.connectmedia.hu/5202/live.mp3",

            ["juventus"] =
                "https://www.radiojuventus.hu/stream_192k",

            ["jazzy"] =
                "https://radio.musorok.org/listen/jazzy/jazzy.mp3"
        };

    public static IReadOnlyDictionary<string, string> List()
    {
        lock (Gate)
        {
            return Load()
                .ToDictionary(
                    pair =>
                        pair.Key,
                    pair =>
                        pair.Value,
                    StringComparer.OrdinalIgnoreCase);
        }
    }

    public static string? GetDisplayName(
        string preset)
    {
        return DisplayNames.TryGetValue(
            preset,
            out string? value)
                ? value
                : null;
    }

    public static string Resolve(
        string presetOrUrl)
    {
        if (
            Uri.TryCreate(
                presetOrUrl,
                UriKind.Absolute,
                out Uri? uri) &&
            (
                uri.Scheme.Equals(
                    Uri.UriSchemeHttp,
                    StringComparison.OrdinalIgnoreCase) ||
                uri.Scheme.Equals(
                    Uri.UriSchemeHttps,
                    StringComparison.OrdinalIgnoreCase)
            ))
        {
            return uri.ToString();
        }

        IReadOnlyDictionary<string, string> presets =
            List();

        if (presets.TryGetValue(
            presetOrUrl,
            out string? url))
        {
            return url;
        }

        throw new ArgumentException(
            $"Unknown radio preset '{presetOrUrl}'.");
    }

    private static Dictionary<string, string> Load()
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(
                PresetPath)!);

        if (!File.Exists(
            PresetPath))
        {
            SaveDefaults();
        }

        try
        {
            string json =
                File.ReadAllText(
                    PresetPath);

            Dictionary<string, string>? parsed =
                JsonSerializer.Deserialize<
                    Dictionary<string, string>>(
                        json);

            if (parsed is not null &&
                parsed.Count != 0)
            {
                var merged =
                    new Dictionary<string, string>(
                        parsed,
                        StringComparer.OrdinalIgnoreCase);

                bool changed =
                    false;

                foreach (
                    KeyValuePair<string, string> preset
                    in Defaults)
                {
                    if (merged.ContainsKey(
                        preset.Key))
                    {
                        continue;
                    }

                    merged[preset.Key] =
                        preset.Value;

                    changed =
                        true;
                }

                if (changed)
                {
                    Save(
                        merged);
                }

                return merged;
            }
        }
        catch
        {
        }

        return new Dictionary<string, string>(
            Defaults,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveDefaults()
    {
        Save(
            Defaults);
    }

    private static void Save(
        IReadOnlyDictionary<string, string> presets)
    {
        string json =
            JsonSerializer.Serialize(
                presets,
                new JsonSerializerOptions
                {
                    WriteIndented =
                        true
                });

        File.WriteAllText(
            PresetPath,
            json);
    }
}
