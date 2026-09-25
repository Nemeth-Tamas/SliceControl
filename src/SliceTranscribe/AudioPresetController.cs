using System.Text.Json;

namespace SliceTranscribe;

internal sealed record AudioPreset(
    int Volume,
    bool Muted,
    string? Radio);

internal static class AudioPresetController
{
    private static readonly object Gate =
        new();

    private static readonly string PresetPath =
        Path.Combine(
            Environment.GetFolderPath(
                Environment.SpecialFolder.LocalApplicationData),
            "SliceAppliance",
            "audio-presets.json");

    private static readonly Dictionary<string, AudioPreset> Defaults =
        new(
            StringComparer.OrdinalIgnoreCase)
        {
            ["shop-daytime"] =
                new(
                    Volume: 30,
                    Muted: false,
                    Radio: "retro"),

            ["quiet"] =
                new(
                    Volume: 15,
                    Muted: false,
                    Radio: "retro"),

            ["closing-time"] =
                new(
                    Volume: 25,
                    Muted: false,
                    Radio: "retro"),

            ["customer-demo"] =
                new(
                    Volume: 50,
                    Muted: false,
                    Radio: "stop"),

            ["night"] =
                new(
                    Volume: 10,
                    Muted: true,
                    Radio: "stop")
        };

    public static IReadOnlyDictionary<string, AudioPreset> List()
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

    public static async Task<AudioPreset> ActivateAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, AudioPreset> presets =
            List();

        if (!presets.TryGetValue(
            name,
            out AudioPreset? preset))
        {
            throw new ArgumentException(
                $"Unknown audio preset '{name}'.");
        }

        SystemAudioController.SetVolumePercent(
            preset.Volume);

        if (!string.IsNullOrWhiteSpace(
            preset.Radio))
        {
            if (preset.Radio.Equals(
                "stop",
                StringComparison.OrdinalIgnoreCase))
            {
                await RadioController.StopAsync(
                    cancellationToken);
            }
            else
            {
                await RadioController.PlayAsync(
                    preset.Radio,
                    cancellationToken);
            }
        }

        SystemAudioController.SetMuted(
            preset.Muted);

        Console.WriteLine(
            $"AUDIO PRESET -> {name}");

        return preset;
    }

    private static Dictionary<string, AudioPreset> Load()
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

            Dictionary<string, AudioPreset>? parsed =
                JsonSerializer.Deserialize<
                    Dictionary<string, AudioPreset>>(
                        json);

            if (parsed is not null &&
                parsed.Count != 0)
            {
                return new Dictionary<string, AudioPreset>(
                    parsed,
                    StringComparer.OrdinalIgnoreCase);
            }
        }
        catch
        {
        }

        return new Dictionary<string, AudioPreset>(
            Defaults,
            StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveDefaults()
    {
        string json =
            JsonSerializer.Serialize(
                Defaults,
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
