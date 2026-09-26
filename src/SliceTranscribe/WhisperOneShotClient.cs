using NAudio.MediaFoundation;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SliceTranscribe;

internal sealed class WhisperOneShotClient :
    IDisposable
{
    private const int TargetSampleRate =
        16000;

    private readonly Uri _inferenceUri;

    private readonly HttpClient _http =
        new()
        {
            Timeout =
                TimeSpan.FromSeconds(
                    30)
        };

    public WhisperOneShotClient(
        string baseUrl)
    {
        string normalized =
            baseUrl.TrimEnd(
                '/') +
            "/";

        _inferenceUri =
            new Uri(
                new Uri(
                    normalized),
                "inference");
    }

    public async Task<string> TranscribeAsync(
        byte[] sourceAudio,
        WaveFormat captureFormat,
        string language,
        string prompt,
        CancellationToken cancellationToken = default)
    {
        WaveFormat sourceFormat =
            captureFormat is WaveFormatExtensible extensible
                ? extensible.ToStandardWaveFormat()
                : captureFormat;

        float[] mono =
            ResampleToMono16k(
                sourceAudio,
                sourceFormat);

        if (mono.Length <
            TargetSampleRate /
            4)
        {
            return string.Empty;
        }

        byte[] wav =
            ConvertToWave(
                mono);

        using var form =
            new MultipartFormDataContent();

        var audio =
            new ByteArrayContent(
                wav);

        audio.Headers.ContentType =
            new MediaTypeHeaderValue(
                "audio/wav");

        form.Add(
            audio,
            "file",
            "assistant.wav");

        AddField(
            form,
            "language",
            language);

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
            "suppress_nst",
            "true");

        AddField(
            form,
            "prompt",
            prompt);

        using HttpResponseMessage response =
            await _http.PostAsync(
                _inferenceUri,
                form,
                cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Assistant Whisper returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return ParseTranscript(
            body)
            .Trim();
    }

    private static float[] ResampleToMono16k(
        byte[] sourceAudio,
        WaveFormat sourceFormat)
    {
        using var rawStream =
            new MemoryStream(
                sourceAudio,
                writable: false);

        using var rawSource =
            new RawSourceWaveStream(
                rawStream,
                sourceFormat);

        ISampleProvider samples =
            rawSource.ToSampleProvider();

        if (samples.WaveFormat.Channels >
            1)
        {
            samples =
                new FixedChannelSampleProvider(
                    samples,
                    0);
        }

        var resampler =
            new WdlResamplingSampleProvider(
                samples,
                TargetSampleRate);

        var output =
            new List<float>();

        float[] buffer =
            new float[4096];

        while (true)
        {
            int read =
                resampler.Read(
                    buffer,
                    0,
                    buffer.Length);

            if (read <= 0)
            {
                break;
            }

            for (int i = 0;
                 i < read;
                 i++)
            {
                output.Add(
                    buffer[i]);
            }
        }

        return output.ToArray();
    }

    private static byte[] ConvertToWave(
        float[] samples)
    {
        using var stream =
            new MemoryStream();

        using (
            var writer =
                new WaveFileWriter(
                    stream,
                    new WaveFormat(
                        TargetSampleRate,
                        16,
                        1)))
        {
            byte[] pcm =
                new byte[
                    samples.Length *
                    2];

            for (int i = 0;
                 i < samples.Length;
                 i++)
            {
                float clamped =
                    Math.Clamp(
                        samples[i],
                        -1f,
                        1f);

                short value =
                    (short)Math.Round(
                        clamped *
                        short.MaxValue);

                pcm[i * 2] =
                    (byte)(
                        value &
                        0xff);

                pcm[(i * 2) + 1] =
                    (byte)(
                        (value >>
                         8) &
                        0xff);
            }

            writer.Write(
                pcm,
                0,
                pcm.Length);

            writer.Flush();
        }

        return stream.ToArray();
    }

    private static string ParseTranscript(
        string body)
    {
        if (string.IsNullOrWhiteSpace(
            body))
        {
            return string.Empty;
        }

        try
        {
            using JsonDocument document =
                JsonDocument.Parse(
                    body);

            JsonElement root =
                document.RootElement;

            if (
                root.ValueKind ==
                    JsonValueKind.Object &&
                root.TryGetProperty(
                    "text",
                    out JsonElement text) &&
                text.ValueKind ==
                    JsonValueKind.String)
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

    public void Dispose()
    {
        _http.Dispose();
    }

    private sealed class FixedChannelSampleProvider :
        ISampleProvider
    {
        private readonly ISampleProvider _source;
        private readonly int _channel;
        private readonly float[] _sourceBuffer =
            new float[4096];

        public FixedChannelSampleProvider(
            ISampleProvider source,
            int channel)
        {
            _source =
                source;

            _channel =
                channel;

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

            int sourceNeeded =
                Math.Min(
                    _sourceBuffer.Length,
                    count *
                    channels);

            sourceNeeded -=
                sourceNeeded %
                channels;

            int read =
                _source.Read(
                    _sourceBuffer,
                    0,
                    sourceNeeded);

            int frames =
                read /
                channels;

            for (int i = 0;
                 i < frames;
                 i++)
            {
                buffer[offset + i] =
                    _sourceBuffer[
                        (i * channels) +
                        _channel];
            }

            return frames;
        }
    }
}
