using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public class TranscriptionService
    {
        private const string FillerPrompt = "Umm, let me think like, hmm... Okay, here's what I'm, like, thinking.";
        private const int MaxAnnotationTokens = 6;

        private readonly IActivityLogger _logger;
        private readonly string _modelPath;

        public class CutSpan
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
        }

        public TranscriptionService(IActivityLogger logger)
        {
            _logger = logger;
            _modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.en.bin");
        }

        private static bool IsAnnotationToken(string token, ref bool inAnnotation, ref int annotationTokenCount, out bool guardTripped)
        {
            guardTripped = false;
            string trimmedEnd = token.TrimEnd('.', ',', '!', '?', ':', ';');
            bool closes = trimmedEnd.EndsWith("]") || trimmedEnd.EndsWith(")");

            if (inAnnotation)
            {
                annotationTokenCount++;
                if (annotationTokenCount > MaxAnnotationTokens)
                {
                    inAnnotation = false;
                    annotationTokenCount = 0;
                    guardTripped = true;
                    return false;               // treat this token as a normal word
                }
                if (closes) { inAnnotation = false; annotationTokenCount = 0; }
                return true;
            }

            if (token.StartsWith("[") || token.StartsWith("("))
            {
                if (!closes) { inAnnotation = true; annotationTokenCount = 0; }
                return true;                    // "(laughs)" is skipped in one step
            }

            if (token.Contains("\u266A") || token.Contains("\u266B") || token.Contains("\u266C") || token.Contains("\u2669"))
                return true;

            return false;
        }

        public async Task<TranscriptionResult> TranscribeAsync(string wavPath, bool useFillerPrompt, bool useGpu, bool wordLevelTimestamps, CancellationToken ct)
        {
            if (!File.Exists(_modelPath))
            {
                _logger.Log($"AI model missing at {_modelPath}. Skipping transcription.");
                throw new FileNotFoundException("Whisper model not found.");
            }

            _logger.Log("Transcribing audio...");

            if (useGpu)
            {
                Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = new List<Whisper.net.LibraryLoader.RuntimeLibrary>
                {
                    Whisper.net.LibraryLoader.RuntimeLibrary.Cuda12,
                    Whisper.net.LibraryLoader.RuntimeLibrary.Vulkan,
                    Whisper.net.LibraryLoader.RuntimeLibrary.Cpu
                };
            }
            else
            {
                Whisper.net.LibraryLoader.RuntimeOptions.RuntimeLibraryOrder = new List<Whisper.net.LibraryLoader.RuntimeLibrary>
                {
                    Whisper.net.LibraryLoader.RuntimeLibrary.Cpu
                };
            }

            var result = new TranscriptionResult();

            await Task.Run(async () =>
            {
                using var whisperFactory = WhisperFactory.FromPath(_modelPath);
                var builder = whisperFactory.CreateBuilder()
                    .WithLanguage("en")
                    .WithNoContext();

                if (wordLevelTimestamps)
                {
                    builder.WithTokenTimestamps()
                           .WithMaxSegmentLength(1)
                           .SplitOnWord();
                }

                if (useFillerPrompt)
                {
                    builder.WithPrompt(FillerPrompt);
                }

                using var processor = builder.Build();
                using var fileStream = File.OpenRead(wavPath);

                var loadedLibrary = Whisper.net.LibraryLoader.RuntimeOptions.LoadedLibrary;
                _logger.Log($"Transcription runtime loaded: {loadedLibrary}");

                var rawWords = new List<WordTiming>();
                bool loggedEstimationWarning = false;

                bool inAnnotation = false;
                int annotationTokenCount = 0;
                bool loggedGuardWarning = false;

                await foreach (var segment in processor.ProcessAsync(fileStream, ct))
                {
                    var text = segment.Text.Trim();
                    if (string.IsNullOrEmpty(text)) continue;

                    var rawTokens = text.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    var keptTokens = new List<string>();

                    foreach (var token in rawTokens)
                    {
                        bool isAnnotation = IsAnnotationToken(token, ref inAnnotation, ref annotationTokenCount, out bool guardTripped);

                        if (guardTripped && !loggedGuardWarning)
                        {
                            _logger.Log("Annotation guard: stray bracket ignored");
                            loggedGuardWarning = true;
                        }

                        if (!isAnnotation)
                        {
                            keptTokens.Add(token);
                        }
                    }

                    var tokens = keptTokens.ToArray();
                    if (tokens.Length == 0) continue;

                    if (tokens.Length == 1)
                    {
                        rawWords.Add(new WordTiming(tokens[0], segment.Start, segment.End, false));
                    }
                    else if (tokens.Length > 1)
                    {
                        if (!loggedEstimationWarning)
                        {
                            _logger.Log("Word-level timestamps not honored; using estimated timings");
                            loggedEstimationWarning = true;
                        }

                        double segmentDuration = (segment.End - segment.Start).TotalSeconds;
                        double tokenDuration = segmentDuration / tokens.Length;

                        for (int i = 0; i < tokens.Length; i++)
                        {
                            var wordStart = segment.Start.Add(TimeSpan.FromSeconds(i * tokenDuration));
                            var wordEnd = wordStart.Add(TimeSpan.FromSeconds(tokenDuration));
                            rawWords.Add(new WordTiming(tokens[i], wordStart, wordEnd, true));
                        }
                    }
                }

                // Normalize the word list
                var normalizedWords = new List<WordTiming>();
                TimeSpan previousEnd = TimeSpan.Zero;
                foreach (var w in rawWords)
                {
                    var start = w.Start;
                    var end = w.End;

                    if (start < previousEnd)
                    {
                        start = previousEnd;
                    }

                    if (start > end)
                    {
                        start = end;
                    }

                    var normalized = new WordTiming(w.Text, start, end, w.Estimated);
                    normalizedWords.Add(normalized);
                    result.Words.Add(normalized);

                    previousEnd = end;
                }

                // Build sentence segments from the words
                var currentWords = new List<string>();
                TimeSpan currentStart = TimeSpan.Zero;
                TimeSpan currentEnd = TimeSpan.Zero;
                int segmentIndex = 0;

                foreach (var word in normalizedWords)
                {
                    if (currentWords.Count == 0)
                    {
                        currentStart = word.Start;
                    }

                    currentWords.Add(word.Text);
                    currentEnd = word.End;

                    var cleanText = word.Text.TrimEnd('\"', '\'', ']', ')', '}', '>');
                    bool endsWithPunctuation = cleanText.EndsWith(".") || cleanText.EndsWith("?") || cleanText.EndsWith("!");
                    bool isTooLong = (currentEnd - currentStart).TotalSeconds > 15;
                    bool hasTooManyWords = currentWords.Count > 40;

                    if (endsWithPunctuation || isTooLong || hasTooManyWords)
                    {
                        result.Segments.Add(new TranscriptSegment(
                            segmentIndex++,
                            currentStart,
                            currentEnd,
                            string.Join(" ", currentWords)
                        ));

                        currentWords.Clear();
                    }
                }

                if (currentWords.Count > 0)
                {
                    result.Segments.Add(new TranscriptSegment(
                        segmentIndex++,
                        currentStart,
                        currentEnd,
                        string.Join(" ", currentWords)
                    ));
                }
            }, ct);

            int estimatedCount = result.Words.Count(w => w.Estimated);
            _logger.Log($"Transcription complete: {result.Words.Count} words ({estimatedCount} estimated), {result.Segments.Count} sentence segments.");
            return result;
        }

        public List<CutSpan> DetectFillerSpans(IReadOnlyList<WordTiming> words)
        {
            var cutSpans = new List<CutSpan>();
            var fillerWords = new HashSet<string> { "um", "umm", "uh", "uhh", "uhm", "erm", "er", "hmm", "hm" };
            int skippedEstimated = 0;

            foreach (var word in words)
            {
                if (word.Estimated)
                {
                    skippedEstimated++;
                    continue;
                }

                double duration = (word.End - word.Start).TotalSeconds;
                if (duration <= 0 || duration > 2.0)
                {
                    continue;
                }

                // trim non-letter/digit/apostrophe characters from both ends
                int startIdx = 0;
                while (startIdx < word.Text.Length && !char.IsLetterOrDigit(word.Text[startIdx]) && word.Text[startIdx] != '\'')
                {
                    startIdx++;
                }

                int endIdx = word.Text.Length - 1;
                while (endIdx >= startIdx && !char.IsLetterOrDigit(word.Text[endIdx]) && word.Text[endIdx] != '\'')
                {
                    endIdx--;
                }

                if (startIdx <= endIdx)
                {
                    string cleanText = word.Text.Substring(startIdx, endIdx - startIdx + 1).ToLower();
                    if (fillerWords.Contains(cleanText))
                    {
                        cutSpans.Add(new CutSpan { Start = word.Start, End = word.End });
                    }
                }
            }

            if (skippedEstimated > 0)
            {
                _logger.Log($"Skipped {skippedEstimated} estimated words during filler detection.");
            }

            // Merge adjacent spans within 0.25s
            var merged = new List<CutSpan>();
            foreach (var span in cutSpans)
            {
                if (merged.Count == 0)
                {
                    merged.Add(span);
                }
                else
                {
                    var last = merged[merged.Count - 1];
                    if ((span.Start - last.End).TotalSeconds <= 0.25)
                    {
                        last.End = span.End; // Extend
                    }
                    else
                    {
                        merged.Add(span);
                    }
                }
            }

            _logger.Log($"Found {merged.Count} filler word sequences to cut.");
            return merged;
        }
    }
}
