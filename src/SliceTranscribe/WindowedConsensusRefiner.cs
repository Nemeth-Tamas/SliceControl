using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace SliceTranscribe;

internal static class WindowedConsensusRefiner
{
    private const int TargetSampleRate =
        16000;

    private static readonly TimeSpan WindowDuration =
        TimeSpan.FromSeconds(
            12);

    public static async Task<string?> RefineAsync(
        string wavPath,
        string whisperServerUrl,
        string? diarizationServerUrl,
        int expectedSpeakers = 2,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(
            wavPath))
        {
            return null;
        }

        int channels;
        TimeSpan totalDuration;

        using (
            var reader =
                new WaveFileReader(
                    wavPath))
        {
            channels =
                reader.WaveFormat.Channels;

            totalDuration =
                reader.TotalTime;
        }

        if (channels <= 0 ||
            totalDuration <=
                TimeSpan.Zero)
        {
            return null;
        }

        Console.WriteLine();
        Console.WriteLine(
            $"FINALIZING -> channel 0 / {WindowDuration.TotalSeconds:0}-second large-v3 windows");

        if (channels > 1)
        {
            Console.WriteLine(
                $"MIC ARRAY -> {channels} exposed channels; using channel 0 because hardware analysis showed bit-identical waveforms.");
        }

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

        var finalSegments =
            new List<TranscriptSegment>();

        var debug =
            new StringBuilder();

        debug.AppendLine(
            "# SliceTranscribe single-channel final ASR debug");

        debug.AppendLine(
            "# HP B&O endpoint exposes six bit-identical channels; only channel 0 is transcribed.");

        debug.AppendLine(
            "# Final ASR uses 12-second windows, Hungarian, temperature 0, no fallback, no vocabulary prompt.");

        debug.AppendLine();

        const int selectedChannel =
            0;

        for (
            TimeSpan windowStart =
                TimeSpan.Zero;
            windowStart <
                totalDuration;
            windowStart +=
                WindowDuration)
        {
            cancellationToken.ThrowIfCancellationRequested();

            TimeSpan remaining =
                totalDuration -
                windowStart;

            TimeSpan duration =
                remaining <
                WindowDuration
                    ? remaining
                    : WindowDuration;

            if (duration <
                TimeSpan.FromMilliseconds(
                    400))
            {
                break;
            }

            Console.WriteLine(
                $"WINDOW -> {FormatTimestamp(windowStart.TotalSeconds)} - {FormatTimestamp((windowStart + duration).TotalSeconds)}");

            byte[] wavBytes =
                RenderChannelWindowToMono16k(
                    wavPath,
                    selectedChannel,
                    windowStart,
                    duration);

            WindowCandidate candidate =
                await TranscribeWindowAsync(
                    http,
                    inferenceUri,
                    wavBytes,
                    selectedChannel,
                    windowStart.TotalSeconds,
                    cancellationToken);

            debug.Append(
                $"## {FormatTimestamp(windowStart.TotalSeconds)} - {FormatTimestamp((windowStart + duration).TotalSeconds)} :: ");

            debug.AppendLine(
                candidate.Text);

            if (candidate.Segments.Count ==
                    0 ||
                candidate.Text.Length ==
                    0)
            {
                Console.WriteLine(
                    "WINDOW <- no usable speech");

                continue;
            }

            finalSegments.AddRange(
                candidate.Segments);

            Console.WriteLine(
                $"WINDOW <- confidence {candidate.Confidence:0.000} / {candidate.Text.Length} chars");
        }

        if (finalSegments.Count ==
            0)
        {
            Console.WriteLine(
                "FINAL PASS -> no usable speech");
            return null;
        }

        finalSegments =
            CleanSegments(
                finalSegments);

        const int diarizationChannel =
            selectedChannel;

        string debugPath =
            Path.Combine(
                Path.GetDirectoryName(
                    wavPath)
                ?? string.Empty,
                Path.GetFileNameWithoutExtension(
                    wavPath) +
                ".channels.txt");

        await File.WriteAllTextAsync(
            debugPath,
            debug.ToString(),
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        Console.WriteLine(
            $"CONSENSUS DEBUG -> {debugPath}");

        List<SpeakerTurn>? speakers =
            null;

        if (!string.IsNullOrWhiteSpace(
            diarizationServerUrl))
        {
            try
            {
                Console.WriteLine(
                    $"DIARIZATION -> channel {diarizationChannel} / expected speakers {expectedSpeakers}");

                byte[] monoWav =
                    RenderChannelWindowToMono16k(
                        wavPath,
                        diarizationChannel,
                        TimeSpan.Zero,
                        totalDuration);

                speakers =
                    await RequestDiarizationAsync(
                        http,
                        diarizationServerUrl,
                        monoWav,
                        expectedSpeakers,
                        cancellationToken);

                int detectedSpeakers =
                    speakers
                        .Select(
                            turn =>
                                turn.Speaker)
                        .Distinct(
                            StringComparer.Ordinal)
                        .Count();

                Console.WriteLine(
                    $"DIARIZATION <- {speakers.Count} turns / {detectedSpeakers} speakers");
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
            diarizationChannel,
            finalSegments,
            speakers,
            cancellationToken);

        Console.WriteLine(
            $"FINAL TRANSCRIPT -> {finalPath}");

        return finalPath;
    }

    private static async Task<WindowCandidate> TranscribeWindowAsync(
        HttpClient http,
        Uri inferenceUri,
        byte[] wavBytes,
        int channel,
        double timeOffset,
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
            $"window-channel-{channel}.wav");

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

        // Zero disables temperature fallback in whisper.cpp.
        AddField(
            form,
            "temperature_inc",
            "0.0");

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
            "token_timestamps",
            "true");

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

        return ParseWindowTranscript(
            body,
            channel,
            timeOffset);
    }

    private static WindowCandidate ParseWindowTranscript(
        string body,
        int channel,
        double timeOffset)
    {
        using JsonDocument document =
            JsonDocument.Parse(
                body);

        JsonElement root =
            document.RootElement;

        var segments =
            new List<TranscriptSegment>();

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
                    timeOffset +
                    ReadDouble(
                        segment,
                        "start");

                double end =
                    timeOffset +
                    ReadDouble(
                        segment,
                        "end");

                double avgLogProb =
                    ReadDouble(
                        segment,
                        "avg_logprob",
                        -5.0);

                double noSpeechProbability =
                    ReadDouble(
                        segment,
                        "no_speech_prob",
                        0.0);

                IReadOnlyList<TranscriptWord> words =
                    ReadLexicalWords(
                        segment,
                        timeOffset);

                double wordProbability =
                    words.Count > 0
                        ? words.Average(
                            word =>
                                word.Probability)
                        : 0.5;

                segments.Add(
                    new TranscriptSegment(
                        start,
                        end,
                        text,
                        avgLogProb,
                        noSpeechProbability,
                        wordProbability,
                        words));
            }
        }

        string combinedText =
            CleanWhitespace(
                string.Join(
                    " ",
                    segments.Select(
                        segment =>
                            segment.Text)));

        double confidence =
            ScoreSegments(
                segments);

        return new WindowCandidate(
            channel,
            combinedText,
            confidence,
            segments);
    }

    private static WindowCandidate? SelectWindowCandidate(
        IReadOnlyList<WindowCandidate> candidates)
    {
        WindowCandidate[] usable =
            candidates
                .Where(
                    candidate =>
                        candidate.Segments.Count >
                            0 &&
                        candidate.Text.Length >
                            0)
                .ToArray();

        if (usable.Length == 0)
        {
            return null;
        }

        if (usable.Length == 1)
        {
            return usable[0];
        }

        double medianLength =
            Median(
                usable.Select(
                    candidate =>
                        (double)candidate.Text.Length));

        WindowCandidate? best =
            null;

        double bestScore =
            double.NegativeInfinity;

        foreach (
            WindowCandidate candidate
            in usable)
        {
            double similarityTotal =
                0;

            int similarityCount =
                0;

            foreach (
                WindowCandidate other
                in usable)
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

            double lengthRatio =
                Math.Max(
                    0.05,
                    candidate.Text.Length /
                    Math.Max(
                        1.0,
                        medianLength));

            double lengthPenalty =
                Math.Min(
                    0.35,
                    0.16 *
                    Math.Abs(
                        Math.Log(
                            lengthRatio)));

            double score =
                0.62 *
                    consensus +
                0.38 *
                    candidate.Confidence -
                lengthPenalty;

            if (score >
                bestScore)
            {
                bestScore =
                    score;

                best =
                    candidate;
            }
        }

        return best;
    }

    private static double ScoreSegments(
        IReadOnlyList<TranscriptSegment> segments)
    {
        if (segments.Count == 0)
        {
            return 0;
        }

        double weighted =
            0;

        double totalWeight =
            0;

        foreach (
            TranscriptSegment segment
            in segments)
        {
            double weight =
                Math.Max(
                    1,
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
            : 0;
    }

    private static List<TranscriptSegment> CleanSegments(
        IReadOnlyList<TranscriptSegment> input)
    {
        var result =
            new List<TranscriptSegment>();

        foreach (
            TranscriptSegment segment
            in input
                .OrderBy(
                    item =>
                        item.Start)
                .ThenBy(
                    item =>
                        item.End))
        {
            if (result.Count > 0)
            {
                TranscriptSegment previous =
                    result[^1];

                if (NormalizeText(
                        previous.Text) ==
                    NormalizeText(
                        segment.Text) &&
                    Math.Abs(
                        previous.Start -
                        segment.Start) <
                        1.0)
                {
                    continue;
                }
            }

            result.Add(
                segment);
        }

        return result;
    }

    private static int SelectDiarizationChannel(
        IReadOnlyList<int> selectedCounts,
        IReadOnlyList<double> confidenceTotals,
        IReadOnlyList<int> confidenceCounts)
    {
        int bestChannel =
            0;

        for (int channel = 1;
             channel < selectedCounts.Count;
             channel++)
        {
            if (selectedCounts[channel] >
                selectedCounts[
                    bestChannel])
            {
                bestChannel =
                    channel;
                continue;
            }

            if (selectedCounts[channel] <
                selectedCounts[
                    bestChannel])
            {
                continue;
            }

            double channelAverage =
                confidenceCounts[channel] > 0
                    ? confidenceTotals[channel] /
                      confidenceCounts[channel]
                    : 0;

            double bestAverage =
                confidenceCounts[
                    bestChannel] > 0
                    ? confidenceTotals[
                          bestChannel] /
                      confidenceCounts[
                          bestChannel]
                    : 0;

            if (channelAverage >
                bestAverage)
            {
                bestChannel =
                    channel;
            }
        }

        return bestChannel;
    }

    private static async Task<List<SpeakerTurn>> RequestDiarizationAsync(
        HttpClient http,
        string serverUrl,
        byte[] wavBytes,
        int expectedSpeakers,
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

        AddField(
            form,
            "num_speakers",
            expectedSpeakers.ToString(
                CultureInfo.InvariantCulture));

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
        int diarizationChannel,
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SpeakerTurn>? speakers,
        CancellationToken cancellationToken)
    {
        var text =
            new StringBuilder();

        text.AppendLine(
            "# SliceTranscribe final transcript");

        text.AppendLine(
            "# Engine: whisper.cpp large-v3 / 12-second single-channel windows");

        text.AppendLine(
            $"# Diarization channel: {diarizationChannel}");

        text.AppendLine(
            speakers is null
                ? "# Diarization: unavailable"
                : "# Diarization: pyannote / lexical-word alignment");

        text.AppendLine();

        if (speakers is null)
        {
            foreach (
                TranscriptSegment segment
                in segments)
            {
                text.Append(
                    '[');

                text.Append(
                    FormatTimestamp(
                        segment.Start));

                text.Append(
                    "] ");

                text.AppendLine(
                    segment.Text);
            }
        }
        else
        {
            foreach (
                DiarizedLine line
                in BuildDiarizedLines(
                    segments,
                    speakers))
            {
                text.Append(
                    '[');

                text.Append(
                    FormatTimestamp(
                        line.Start));

                text.Append(
                    "] ");

                if (!string.IsNullOrWhiteSpace(
                    line.Speaker))
                {
                    text.Append(
                        line.Speaker);

                    text.Append(
                        ": ");
                }

                text.AppendLine(
                    line.Text);
            }
        }

        await File.WriteAllTextAsync(
            path,
            text.ToString(),
            new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static List<DiarizedLine> BuildDiarizedLines(
        IReadOnlyList<TranscriptSegment> segments,
        IReadOnlyList<SpeakerTurn> speakers)
    {
        var words =
            new List<DiarizedWord>();

        foreach (
            TranscriptSegment segment
            in segments)
        {
            if (segment.Words.Count == 0)
            {
                words.Add(
                    new DiarizedWord(
                        segment.Start,
                        segment.End,
                        segment.Text,
                        FindSpeaker(
                            segment.Start,
                            segment.End,
                            speakers)));

                continue;
            }

            foreach (
                TranscriptWord word
                in segment.Words)
            {
                string cleaned =
                    CleanWhitespace(
                        word.Text);

                if (cleaned.Length == 0)
                {
                    continue;
                }

                words.Add(
                    new DiarizedWord(
                        word.Start,
                        word.End,
                        cleaned,
                        FindSpeaker(
                            word.Start,
                            word.End,
                            speakers)));
            }
        }

        var lines =
            new List<DiarizedLine>();

        foreach (
            DiarizedWord word
            in words
                .OrderBy(
                    item =>
                        item.Start)
                .ThenBy(
                    item =>
                        item.End))
        {
            if (lines.Count == 0)
            {
                lines.Add(
                    new DiarizedLine(
                        word.Start,
                        word.End,
                        word.Speaker,
                        word.Text));

                continue;
            }

            DiarizedLine previous =
                lines[^1];

            bool sameSpeaker =
                string.Equals(
                    previous.Speaker,
                    word.Speaker,
                    StringComparison.Ordinal);

            bool closeEnough =
                word.Start <=
                    previous.End +
                    1.25;

            if (sameSpeaker &&
                closeEnough)
            {
                lines[^1] =
                    previous with
                    {
                        End =
                            Math.Max(
                                previous.End,
                                word.End),
                        Text =
                            JoinTranscriptText(
                                previous.Text,
                                word.Text)
                    };

                continue;
            }

            lines.Add(
                new DiarizedLine(
                    word.Start,
                    word.End,
                    word.Speaker,
                    word.Text));
        }

        return lines;
    }

    private static IReadOnlyList<TranscriptWord> ReadLexicalWords(
        JsonElement segment,
        double timeOffset)
    {
        var result =
            new List<TranscriptWord>();

        if (!segment.TryGetProperty(
                "words",
                out JsonElement tokens) ||
            tokens.ValueKind !=
                JsonValueKind.Array)
        {
            return result;
        }

        var currentText =
            new StringBuilder();

        double currentStart =
            -1;

        double currentEnd =
            -1;

        double probabilityTotal =
            0;

        int probabilityCount =
            0;

        void FlushCurrent()
        {
            if (currentText.Length == 0)
            {
                return;
            }

            string text =
                currentText
                    .ToString()
                    .Trim();

            if (text.Length > 0 &&
                currentStart >= 0 &&
                currentEnd >=
                    currentStart)
            {
                result.Add(
                    new TranscriptWord(
                        currentStart,
                        currentEnd,
                        text,
                        probabilityCount > 0
                            ? probabilityTotal /
                              probabilityCount
                            : 0.5));
            }

            currentText.Clear();

            currentStart =
                -1;

            currentEnd =
                -1;

            probabilityTotal =
                0;

            probabilityCount =
                0;
        }

        foreach (
            JsonElement token
            in tokens.EnumerateArray())
        {
            if (!token.TryGetProperty(
                    "word",
                    out JsonElement textElement))
            {
                continue;
            }

            string piece =
                textElement.GetString()
                ?? string.Empty;

            if (piece.Length == 0)
            {
                continue;
            }

            double start =
                ReadDouble(
                    token,
                    "start",
                    -1);

            double end =
                ReadDouble(
                    token,
                    "end",
                    -1);

            if (start < 0 ||
                end < start)
            {
                continue;
            }

            start +=
                timeOffset;

            end +=
                timeOffset;

            double probability =
                ReadDouble(
                    token,
                    "probability",
                    0.5);

            bool beginsWithWhitespace =
                char.IsWhiteSpace(
                    piece[0]);

            string trimmedPiece =
                piece.TrimStart();

            bool punctuationOnly =
                trimmedPiece.Length > 0 &&
                trimmedPiece.All(
                    character =>
                        char.IsPunctuation(
                            character) ||
                        char.IsSymbol(
                            character));

            if (currentText.Length > 0 &&
                beginsWithWhitespace &&
                !punctuationOnly)
            {
                FlushCurrent();
            }

            if (currentText.Length == 0)
            {
                currentStart =
                    start;

                currentText.Append(
                    trimmedPiece);
            }
            else
            {
                currentText.Append(
                    beginsWithWhitespace
                        ? trimmedPiece
                        : piece);
            }

            currentEnd =
                Math.Max(
                    currentEnd,
                    end);

            probabilityTotal +=
                probability;

            probabilityCount++;
        }

        FlushCurrent();

        return result;
    }

    private static byte[] RenderChannelWindowToMono16k(
        string wavPath,
        int selectedChannel,
        TimeSpan skip,
        TimeSpan take)
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

        ISampleProvider window =
            new OffsetSampleProvider(
                selected)
            {
                SkipOver =
                    skip,
                Take =
                    take
            };

        var resampler =
            new WdlResamplingSampleProvider(
                window,
                TargetSampleRate);

        using var output =
            new MemoryStream();

        WaveFileWriter.WriteWavFileToStream(
            output,
            resampler.ToWaveProvider16());

        return output.ToArray();
    }

    private static string? FindSpeaker(
        double start,
        double end,
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
                        end,
                        turn.End) -
                    Math.Max(
                        start,
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

        if (best is not null)
        {
            return best.Speaker;
        }

        double midpoint =
            (start +
             end) /
            2.0;

        return speakers
            .OrderBy(
                turn =>
                {
                    double turnMidpoint =
                        (turn.Start +
                         turn.End) /
                        2.0;

                    return Math.Abs(
                        turnMidpoint -
                        midpoint);
                })
            .FirstOrDefault()
            ?.Speaker;
    }

    private static string JoinTranscriptText(
        string left,
        string right)
    {
        string leftTrimmed =
            left.TrimEnd();

        string rightTrimmed =
            right.TrimStart();

        if (leftTrimmed.Length == 0)
        {
            return rightTrimmed;
        }

        if (rightTrimmed.Length == 0)
        {
            return leftTrimmed;
        }

        char first =
            rightTrimmed[0];

        char last =
            leftTrimmed[^1];

        bool attachToPrevious =
            IsClosingPunctuation(
                first);

        bool attachToNext =
            IsOpeningPunctuation(
                last);

        return
            leftTrimmed +
            (
                attachToPrevious ||
                attachToNext
                    ? string.Empty
                    : " "
            ) +
            rightTrimmed;
    }

    private static bool IsClosingPunctuation(
        char character)
    {
        return character is
            '.' or
            ',' or
            '!' or
            '?' or
            ':' or
            ';' or
            ')' or
            ']' or
            '}' or
            '%' or
            '…';
    }

    private static bool IsOpeningPunctuation(
        char character)
    {
        return character is
            '(' or
            '[' or
            '{';
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
            ? (
                ordered[
                    middle -
                    1] +
                ordered[
                    middle]
              ) /
              2
            : ordered[
                middle];
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

    private sealed record WindowCandidate(
        int Channel,
        string Text,
        double Confidence,
        IReadOnlyList<TranscriptSegment> Segments);

    private sealed record TranscriptSegment(
        double Start,
        double End,
        string Text,
        double AvgLogProb,
        double NoSpeechProbability,
        double WordProbability,
        IReadOnlyList<TranscriptWord> Words);

    private sealed record TranscriptWord(
        double Start,
        double End,
        string Text,
        double Probability);

    private sealed record SpeakerTurn(
        double Start,
        double End,
        string Speaker);

    private sealed record DiarizedWord(
        double Start,
        double End,
        string Text,
        string? Speaker);

    private sealed record DiarizedLine(
        double Start,
        double End,
        string? Speaker,
        string Text);
}
