using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SliceTranscribe;

internal static class FinalTranscriptRefiner
{
    private const int TargetSampleRate =
        16000;

    private const string Prompt =
        "Cellnet, Dunaújváros, iPhone, Android, Windows, Samsung, Xiaomi, USB, SSD, RAM, Wi-Fi.";

    public static async Task<string?> RefineAsync(
        string wavPath,
        string whisperServerUrl,
        string? diarizationServerUrl,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(
            wavPath))
        {
            return null;
        }

        using var reader =
            new WaveFileReader(
                wavPath);

        int channels =
            reader.WaveFormat.Channels;

        if (channels <= 0)
        {
            return null;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"FINALIZING -> {channels}-channel large-v3 consensus");

        using var http =
            new HttpClient
            {
                Timeout =
                    TimeSpan.FromMinutes(
                        10)
            };

        Uri inferenceUri =
            new(
                whisperServerUrl.TrimEnd('/') +
                "/inference");

        var channelResults =
            new List<ChannelTranscript>(
                channels);

        for (int channel = 0;
             channel < channels;
             channel++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            Console.WriteLine(
                $"FINAL PASS -> channel {channel + 1}/{channels}");

            byte[] monoWav =
                RenderChannelToMono16k(
                    wavPath,
                    channel);

            ChannelTranscript result =
                await TranscribeChannelAsync(
                    http,
                    inferenceUri,
                    monoWav,
                    channel,
                    cancellationToken);

            channelResults.Add(
                result);

            Console.WriteLine(
                $"FINAL PASS <- channel {channel}: {result.Segments.Count} segments, {result.TextLength} chars, confidence {result.Score:0.000}");
        }

        ChannelTranscript reference =
            SelectReferenceChannel(
                channelResults);

        Console.WriteLine(
            $"CONSENSUS -> reference channel {reference.Channel}");

        List<FusedSegment> fused =
            Fuse(
                reference,
                channelResults);

        if (fused.Count == 0)
        {
            Console.WriteLine(
                "FINAL PASS -> no usable speech");
            return null;
        }

        List<SpeakerTurn>? speakers =
            null;

        if (!string.IsNullOrWhiteSpace(
            diarizationServerUrl))
        {
            try
            {
                Console.WriteLine(
                    "DIARIZATION -> requesting speaker turns");

                byte[] monoWav =
                    RenderChannelToMono16k(
                        wavPath,
                        reference.Channel);

                speakers =
                    await RequestDiarizationAsync(
                        http,
                        diarizationServerUrl,
                        monoWav,
                        cancellationToken);

                Console.WriteLine(
                    $"DIARIZATION <- {speakers.Count} speaker turns");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(
                    $"Diarization unavailable: {ex.Message}");

                Console.Error.WriteLine(
                    "Final transcript will be written without speaker labels.");
            }
        }

        string finalPath =
            Path.Combine(
                Path.GetDirectoryName(
                    wavPath)
                ?? string.Empty,
                Path.GetFileNameWithoutExtension(
                    wavPath) +
                ".final.txt");

        await WriteFinalTranscriptAsync(
            finalPath,
            reference.Channel,
            fused,
            speakers,
            cancellationToken);

        Console.WriteLine(
            $"FINAL TRANSCRIPT -> {finalPath}");

        return finalPath;
    }

    private static async Task<ChannelTranscript> TranscribeChannelAsync(
        HttpClient http,
        Uri inferenceUri,
        byte[] wavBytes,
        int channel,
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
            $"channel-{channel}.wav");

        AddField(
            form,
            "language",
            "hu");

        AddField(
            form,
            "response_format",
            "verbose_json");

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
                $"Whisper channel {channel} returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        return ParseVerboseTranscript(
            body,
            channel);
    }

    private static ChannelTranscript ParseVerboseTranscript(
        string body,
        int channel)
    {
        using JsonDocument document =
            JsonDocument.Parse(
                body);

        JsonElement root =
            document.RootElement;

        var segments =
            new List<WhisperSegment>();

        if (root.TryGetProperty(
                "segments",
                out JsonElement segmentArray) &&
            segmentArray.ValueKind ==
                JsonValueKind.Array)
        {
            foreach (
                JsonElement segment
                in segmentArray.EnumerateArray())
            {
                string text =
                    segment.TryGetProperty(
                        "text",
                        out JsonElement textElement)
                        ? textElement.GetString()
                            ?? string.Empty
                        : string.Empty;

                text =
                    CleanWhitespace(
                        text);

                if (text.Length == 0)
                {
                    continue;
                }

                double start =
                    ReadDouble(
                        segment,
                        "start");

                double end =
                    ReadDouble(
                        segment,
                        "end");

                double avgLogProb =
                    ReadDouble(
                        segment,
                        "avg_logprob",
                        -5.0);

                double noSpeechProb =
                    ReadDouble(
                        segment,
                        "no_speech_prob",
                        0.0);

                double wordProbability =
                    ReadAverageWordProbability(
                        segment);

                segments.Add(
                    new WhisperSegment(
                        channel,
                        start,
                        end,
                        text,
                        avgLogProb,
                        noSpeechProb,
                        wordProbability));
            }
        }

        double score =
            ScoreChannel(
                segments);

        return new ChannelTranscript(
            channel,
            segments,
            score,
            segments.Sum(
                segment =>
                    segment.Text.Length));
    }

    private static ChannelTranscript SelectReferenceChannel(
        IReadOnlyList<ChannelTranscript> channels)
    {
        int maxTextLength =
            Math.Max(
                1,
                channels.Max(
                    channel =>
                        channel.TextLength));

        return channels
            .OrderByDescending(
                channel =>
                {
                    double coverage =
                        (double)channel.TextLength /
                        maxTextLength;

                    return
                        0.75 *
                            channel.Score +
                        0.25 *
                            coverage;
                })
            .First();
    }

    private static double ScoreChannel(
        IReadOnlyList<WhisperSegment> segments)
    {
        if (segments.Count == 0)
        {
            return double.NegativeInfinity;
        }

        double weighted =
            0;

        double totalWeight =
            0;

        foreach (
            WhisperSegment segment
            in segments)
        {
            double weight =
                Math.Max(
                    1.0,
                    segment.Text.Length);

            double confidence =
                0.55 *
                    segment.WordProbability +
                0.35 *
                    Math.Exp(
                        Math.Clamp(
                            segment.AvgLogProb,
                            -5.0,
                            0.0)) +
                0.10 *
                    (1.0 -
                     Math.Clamp(
                         segment.NoSpeechProbability,
                         0.0,
                         1.0));

            weighted +=
                confidence *
                weight;

            totalWeight +=
                weight;
        }

        return totalWeight > 0
            ? weighted /
              totalWeight
            : double.NegativeInfinity;
    }

    private static List<FusedSegment> Fuse(
        ChannelTranscript reference,
        IReadOnlyList<ChannelTranscript> allChannels)
    {
        var output =
            new List<FusedSegment>();

        foreach (
            WhisperSegment anchor
            in reference.Segments)
        {
            var candidates =
                new List<WhisperSegment>
                {
                    anchor
                };

            foreach (
                ChannelTranscript channel
                in allChannels)
            {
                if (channel.Channel ==
                    reference.Channel)
                {
                    continue;
                }

                WhisperSegment? best =
                    channel.Segments
                        .Select(
                            segment =>
                                new
                                {
                                    Segment =
                                        segment,
                                    Iou =
                                        TimeIntersectionOverUnion(
                                            anchor,
                                            segment),
                                    ShorterOverlap =
                                        TimeOverlapOnShorter(
                                            anchor,
                                            segment)
                                })
                        .Where(
                            item =>
                                item.Iou >=
                                    0.25 ||
                                item.ShorterOverlap >=
                                    0.55)
                        .OrderByDescending(
                            item =>
                                Math.Max(
                                    item.Iou,
                                    item.ShorterOverlap))
                        .Select(
                            item =>
                                item.Segment)
                        .FirstOrDefault();

                if (best is not null)
                {
                    candidates.Add(
                        best);
                }
            }

            WhisperSegment selected =
                SelectConsensusCandidate(
                    candidates);

            double start =
                Median(
                    candidates.Select(
                        candidate =>
                            candidate.Start));

            double end =
                Median(
                    candidates.Select(
                        candidate =>
                            candidate.End));

            output.Add(
                new FusedSegment(
                    start,
                    end,
                    selected.Text,
                    candidates.Count,
                    selected.Channel));
        }

        return MergeExactAdjacentDuplicates(
            output);
    }

    private static WhisperSegment SelectConsensusCandidate(
        IReadOnlyList<WhisperSegment> candidates)
    {
        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        WhisperSegment? best =
            null;

        double bestScore =
            double.NegativeInfinity;

        foreach (
            WhisperSegment candidate
            in candidates)
        {
            double similarityTotal =
                0;

            int similarityCount =
                0;

            foreach (
                WhisperSegment other
                in candidates)
            {
                if (ReferenceEquals(
                    candidate,
                    other))
                {
                    continue;
                }

                similarityTotal +=
                    TextSimilarity(
                        candidate.Text,
                        other.Text);

                similarityCount++;
            }

            double consensus =
                similarityCount > 0
                    ? similarityTotal /
                      similarityCount
                    : 0;

            double acousticConfidence =
                0.60 *
                    candidate.WordProbability +
                0.30 *
                    Math.Exp(
                        Math.Clamp(
                            candidate.AvgLogProb,
                            -5.0,
                            0.0)) +
                0.10 *
                    (1.0 -
                     Math.Clamp(
                         candidate.NoSpeechProbability,
                         0.0,
                         1.0));

            double score =
                0.70 *
                    consensus +
                0.30 *
                    acousticConfidence;

            if (score >
                bestScore)
            {
                bestScore =
                    score;

                best =
                    candidate;
            }
        }

        return best
            ?? candidates[0];
    }

    private static List<FusedSegment> MergeExactAdjacentDuplicates(
        IReadOnlyList<FusedSegment> input)
    {
        var output =
            new List<FusedSegment>();

        foreach (
            FusedSegment segment
            in input)
        {
            if (output.Count == 0)
            {
                output.Add(
                    segment);
                continue;
            }

            FusedSegment previous =
                output[^1];

            if (NormalizeText(
                    previous.Text) ==
                NormalizeText(
                    segment.Text))
            {
                output[^1] =
                    previous with
                    {
                        End =
                            Math.Max(
                                previous.End,
                                segment.End),
                        Votes =
                            Math.Max(
                                previous.Votes,
                                segment.Votes)
                    };

                continue;
            }

            output.Add(
                segment);
        }

        return output;
    }

    private static async Task<List<SpeakerTurn>> RequestDiarizationAsync(
        HttpClient http,
        string serverUrl,
        byte[] wavBytes,
        CancellationToken cancellationToken)
    {
        Uri uri =
            new(
                serverUrl.TrimEnd('/') +
                "/diarize");

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
            "recording.wav");

        using HttpResponseMessage response =
            await http.PostAsync(
                uri,
                form,
                cancellationToken);

        string body =
            await response.Content.ReadAsStringAsync(
                cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Diarization server returned {(int)response.StatusCode} {response.ReasonPhrase}: {body}");
        }

        using JsonDocument document =
            JsonDocument.Parse(
                body);

        JsonElement root =
            document.RootElement;

        if (!root.TryGetProperty(
                "segments",
                out JsonElement segments) ||
            segments.ValueKind !=
                JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "Diarization response did not contain a segments array.");
        }

        var result =
            new List<SpeakerTurn>();

        foreach (
            JsonElement item
            in segments.EnumerateArray())
        {
            string? speaker =
                item.TryGetProperty(
                    "speaker",
                    out JsonElement speakerElement)
                    ? speakerElement.GetString()
                    : null;

            if (string.IsNullOrWhiteSpace(
                speaker))
            {
                continue;
            }

            result.Add(
                new SpeakerTurn(
                    ReadDouble(
                        item,
                        "start"),
                    ReadDouble(
                        item,
                        "end"),
                    speaker));
        }

        return result;
    }

    private static async Task WriteFinalTranscriptAsync(
        string path,
        int referenceChannel,
        IReadOnlyList<FusedSegment> segments,
        IReadOnlyList<SpeakerTurn>? speakers,
        CancellationToken cancellationToken)
    {
        var text =
            new StringBuilder();

        text.AppendLine(
            "# SliceTranscribe final transcript");

        text.AppendLine(
            "# Engine: whisper.cpp large-v3 / six-channel consensus");

        text.AppendLine(
            $"# Reference channel: {referenceChannel}");

        text.AppendLine(
            speakers is null
                ? "# Diarization: unavailable"
                : "# Diarization: pyannote");

        text.AppendLine();

        foreach (
            FusedSegment segment
            in segments)
        {
            string? speaker =
                speakers is null
                    ? null
                    : FindSpeaker(
                        segment,
                        speakers);

            text.Append(
                '[');

            text.Append(
                FormatTimestamp(
                    segment.Start));

            text.Append(
                "] ");

            if (!string.IsNullOrWhiteSpace(
                speaker))
            {
                text.Append(
                    speaker);

                text.Append(
                    ": ");
            }

            text.AppendLine(
                segment.Text);
        }

        await File.WriteAllTextAsync(
            path,
            text.ToString(),
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static string? FindSpeaker(
        FusedSegment segment,
        IReadOnlyList<SpeakerTurn> speakers)
    {
        SpeakerTurn? best =
            null;

        double bestOverlap =
            0;

        foreach (
            SpeakerTurn turn
            in speakers)
        {
            double overlap =
                Math.Max(
                    0,
                    Math.Min(
                        segment.End,
                        turn.End) -
                    Math.Max(
                        segment.Start,
                        turn.Start));

            if (overlap >
                bestOverlap)
            {
                bestOverlap =
                    overlap;

                best =
                    turn;
            }
        }

        return best?.Speaker;
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

    private static double ReadAverageWordProbability(
        JsonElement segment)
    {
        if (!segment.TryGetProperty(
                "words",
                out JsonElement words) ||
            words.ValueKind !=
                JsonValueKind.Array)
        {
            return 0.5;
        }

        double total =
            0;

        int count =
            0;

        foreach (
            JsonElement word
            in words.EnumerateArray())
        {
            if (!word.TryGetProperty(
                    "probability",
                    out JsonElement probability) ||
                !probability.TryGetDouble(
                    out double value))
            {
                continue;
            }

            total +=
                value;

            count++;
        }

        return count > 0
            ? total /
              count
            : 0.5;
    }

    private static double ReadDouble(
        JsonElement element,
        string property,
        double fallback = 0)
    {
        return element.TryGetProperty(
                property,
                out JsonElement value) &&
            value.TryGetDouble(
                out double result)
            ? result
            : fallback;
    }

    private static double TimeOverlapOnShorter(
        WhisperSegment left,
        WhisperSegment right)
    {
        double intersection =
            Math.Max(
                0,
                Math.Min(
                    left.End,
                    right.End) -
                Math.Max(
                    left.Start,
                    right.Start));

        double shorter =
            Math.Min(
                Math.Max(
                    0.001,
                    left.End -
                    left.Start),
                Math.Max(
                    0.001,
                    right.End -
                    right.Start));

        return intersection /
            shorter;
    }

    private static double TimeIntersectionOverUnion(
        WhisperSegment left,
        WhisperSegment right)
    {
        double intersection =
            Math.Max(
                0,
                Math.Min(
                    left.End,
                    right.End) -
                Math.Max(
                    left.Start,
                    right.Start));

        double union =
            Math.Max(
                left.End,
                right.End) -
            Math.Min(
                left.Start,
                right.Start);

        return union > 0
            ? intersection /
              union
            : 0;
    }

    private static double TextSimilarity(
        string left,
        string right)
    {
        string a =
            NormalizeText(
                left);

        string b =
            NormalizeText(
                right);

        if (a.Length == 0 ||
            b.Length == 0)
        {
            return 0;
        }

        if (a == b)
        {
            return 1;
        }

        int distance =
            LevenshteinDistance(
                a,
                b);

        return 1.0 -
            (double)distance /
            Math.Max(
                a.Length,
                b.Length);
    }

    private static int LevenshteinDistance(
        string left,
        string right)
    {
        var previous =
            new int[
                right.Length +
                1];

        var current =
            new int[
                right.Length +
                1];

        for (int j = 0;
             j <= right.Length;
             j++)
        {
            previous[j] =
                j;
        }

        for (int i = 1;
             i <= left.Length;
             i++)
        {
            current[0] =
                i;

            for (int j = 1;
                 j <= right.Length;
                 j++)
            {
                int substitution =
                    left[
                        i -
                        1] ==
                    right[
                        j -
                        1]
                        ? 0
                        : 1;

                current[j] =
                    Math.Min(
                        Math.Min(
                            current[
                                j -
                                1] +
                            1,
                            previous[j] +
                            1),
                        previous[
                            j -
                            1] +
                        substitution);
            }

            (previous, current) =
                (current, previous);
        }

        return previous[
            right.Length];
    }

    private static string NormalizeText(
        string value)
    {
        string decomposed =
            value
                .ToLowerInvariant()
                .Normalize(
                    NormalizationForm.FormD);

        var builder =
            new StringBuilder(
                decomposed.Length);

        foreach (
            char character
            in decomposed)
        {
            UnicodeCategory category =
                CharUnicodeInfo.GetUnicodeCategory(
                    character);

            if (category ==
                UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsLetterOrDigit(
                    character) ||
                char.IsWhiteSpace(
                    character))
            {
                builder.Append(
                    character);
            }
        }

        return CleanWhitespace(
            builder
                .ToString()
                .Normalize(
                    NormalizationForm.FormC));
    }

    private static string CleanWhitespace(
        string value)
    {
        return string.Join(
            ' ',
            value.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries));
    }

    private static double Median(
        IEnumerable<double> values)
    {
        double[] ordered =
            values
                .OrderBy(
                    value =>
                        value)
                .ToArray();

        if (ordered.Length == 0)
        {
            return 0;
        }

        int middle =
            ordered.Length /
            2;

        return ordered.Length % 2 == 0
            ? (ordered[
                   middle -
                   1] +
               ordered[
                   middle]) /
              2
            : ordered[
                middle];
    }

    private static string FormatTimestamp(
        double seconds)
    {
        TimeSpan value =
            TimeSpan.FromSeconds(
                Math.Max(
                    0,
                    seconds));

        return value.TotalHours >= 1
            ? value.ToString(
                @"hh\:mm\:ss\.ff",
                CultureInfo.InvariantCulture)
            : value.ToString(
                @"mm\:ss\.ff",
                CultureInfo.InvariantCulture);
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

    private sealed record ChannelTranscript(
        int Channel,
        IReadOnlyList<WhisperSegment> Segments,
        double Score,
        int TextLength);

    private sealed record WhisperSegment(
        int Channel,
        double Start,
        double End,
        string Text,
        double AvgLogProb,
        double NoSpeechProbability,
        double WordProbability);

    private sealed record FusedSegment(
        double Start,
        double End,
        string Text,
        int Votes,
        int SourceChannel);

    private sealed record SpeakerTurn(
        double Start,
        double End,
        string Speaker);
}
