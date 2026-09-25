using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SliceTranscribe;

internal static class ChannelProbe
{
    private const int TargetSampleRate =
        16000;

    private const string Prompt =
        "Cellnet, Dunaújváros, iPhone, Android, Windows, Samsung, Xiaomi, USB, SSD, RAM, Wi-Fi.";

    public static async Task RunAsync(
        string wavPath,
        string serverUrl,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(
            wavPath))
        {
            throw new FileNotFoundException(
                "Probe WAV was not found.",
                wavPath);
        }

        using var infoReader =
            new WaveFileReader(
                wavPath);

        int channels =
            infoReader.WaveFormat.Channels;

        Console.WriteLine(
            $"Probe source: {infoReader.WaveFormat}");

        Console.WriteLine(
            $"Testing {channels} channels against {serverUrl}...");

        using var http =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromSeconds(60)
            };

        Uri inferenceUri =
            new(
                serverUrl.TrimEnd('/') +
                "/inference");

        for (int channel = 0;
             channel < channels;
             channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            byte[] monoWav =
                RenderChannelToMono16k(
                    wavPath,
                    channel);

            string transcript =
                await TranscribeAsync(
                    http,
                    inferenceUri,
                    monoWav,
                    cancellationToken);

            Console.WriteLine();
            Console.WriteLine(
                $"CHANNEL {channel}:");

            Console.WriteLine(
                string.IsNullOrWhiteSpace(
                    transcript)
                    ? "(no text)"
                    : transcript.Trim());
        }
    }

    private static byte[] RenderChannelToMono16k(
        string wavPath,
        int selectedChannel)
    {
        using var reader =
            new WaveFileReader(
                wavPath);

        ISampleProvider source =
            reader.ToSampleProvider();

        if (selectedChannel < 0 ||
            selectedChannel >=
                source.WaveFormat.Channels)
        {
            throw new ArgumentOutOfRangeException(
                nameof(selectedChannel));
        }

        ISampleProvider selected =
            source.WaveFormat.Channels == 1
                ? source
                : new FixedChannelSampleProvider(
                    source,
                    selectedChannel);

        var resampler =
            new WdlResamplingSampleProvider(
                selected,
                TargetSampleRate);

        using var output =
            new MemoryStream();

        WaveFileWriter.WriteWavFileToStream(
            output,
            resampler.ToWaveProvider16());

        return output.ToArray();
    }

    private static async Task<string> TranscribeAsync(
        HttpClient http,
        Uri inferenceUri,
        byte[] wavBytes,
        CancellationToken cancellationToken)
    {
        using var form =
            new MultipartFormDataContent();

        var audio =
            new ByteArrayContent(
                wavBytes);

        audio.Headers.ContentType =
            new MediaTypeHeaderValue(
                "audio/wav");

        form.Add(
            audio,
            "file",
            "probe.wav");

        AddField(
            form,
            "language",
            "hu");

        AddField(
            form,
            "response_format",
            "json");

        AddField(
            form,
            "temperature",
            "0.0");

        AddField(
            form,
            "temperature_inc",
            "0.2");

        AddField(
            form,
            "suppress_nst",
            "true");

        AddField(
            form,
            "no_speech_thold",
            "0.60");

        AddField(
            form,
            "prompt",
            Prompt);

        using HttpResponseMessage response =
            await http.PostAsync(
                inferenceUri,
                form,
                cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Whisper server returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        try
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    body);

            JsonElement root =
                document.RootElement;

            if (root.ValueKind ==
                    JsonValueKind.Object &&
                root.TryGetProperty(
                    "text",
                    out JsonElement text))
            {
                return text.GetString()
                    ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }

        return body.Trim();
    }

    private static void AddField(
        MultipartFormDataContent form,
        string name,
        string value)
    {
        form.Add(
            new StringContent(
                value,
                Encoding.UTF8),
            name);
    }

    private sealed class FixedChannelSampleProvider :
        ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _selectedChannel;

        private float[] _sourceBuffer =
            Array.Empty<float>();

        public FixedChannelSampleProvider(
            ISampleProvider source,
            int selectedChannel)
        {
            _source =
                source;

            _selectedChannel =
                selectedChannel;

            WaveFormat =
                WaveFormat.CreateIeeeFloatWaveFormat(
                    source.WaveFormat.SampleRate,
                    1);
        }

        public WaveFormat WaveFormat { get; }

        public int Read(
            float[] buffer,
            int offset,
            int count)
        {
            int channels =
                _source.WaveFormat.Channels;

            int sourceSamplesNeeded =
                count *
                channels;

            if (_sourceBuffer.Length <
                sourceSamplesNeeded)
            {
                _sourceBuffer =
                    new float[
                        sourceSamplesNeeded];
            }

            int sourceSamplesRead =
                _source.Read(
                    _sourceBuffer,
                    0,
                    sourceSamplesNeeded);

            int frames =
                sourceSamplesRead /
                channels;

            for (int frame = 0;
                 frame < frames;
                 frame++)
            {
                buffer[
                    offset +
                    frame] =
                    _sourceBuffer[
                        frame *
                            channels +
                        _selectedChannel];
            }

            return frames;
        }
    }
}
