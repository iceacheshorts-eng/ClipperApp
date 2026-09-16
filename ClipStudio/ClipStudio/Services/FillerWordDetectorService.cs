using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace ClipStudio.Services
{
    public class FillerWordDetectorService
    {
        private readonly IActivityLogger _logger;
        private readonly string _modelPath;

        public class CutSpan
        {
            public TimeSpan Start { get; set; }
            public TimeSpan End { get; set; }
        }

        public FillerWordDetectorService(IActivityLogger logger)
        {
            _logger = logger;
            _modelPath = Path.Combine(AppContext.BaseDirectory, "Models", "ggml-base.en.bin");
        }

        public async Task<List<CutSpan>> DetectFillerWordsAsync(string wavPath, CancellationToken cancellationToken)
        {
            var cutSpans = new List<CutSpan>();

            if (!File.Exists(_modelPath))
            {
                _logger.Log($"AI model missing at {_modelPath}. Skipping Filler Word detection.");
                throw new FileNotFoundException("Whisper model not found.");
            }

            _logger.Log("Detecting filler words...");

            var fillerWords = new HashSet<string> { "um", "uh", "erm", "hmm", " um", " uh", " erm", " hmm" };

            await Task.Run(async () =>
            {
                using var whisperFactory = WhisperFactory.FromPath(_modelPath);
                using var processor = whisperFactory.CreateBuilder()
                    .WithLanguage("en")
                    .Build(); // Note: Word-level timestamps require specific Whisper.net configurations/models sometimes, but we simulate standard usage here.

                using var fileStream = File.OpenRead(wavPath);

                await foreach (var segment in processor.ProcessAsync(fileStream, cancellationToken))
                {
                    // Whisper.net typically outputs segment level unless word_timestamps are heavily configured.
                    // To extract filler words effectively without full token level access easily,
                    // we analyze the segment text and roughly split timestamps if it contains a filler.

                    var cleanText = segment.Text.ToLower().Replace(".", "").Replace(",", "").Replace("?", "").Trim();
                    var words = cleanText.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                    if (words.Length == 0) continue;

                    double segmentDuration = (segment.End - segment.Start).TotalSeconds;
                    double wordDuration = segmentDuration / words.Length; // Rough approximation for time-slicing

                    for (int i = 0; i < words.Length; i++)
                    {
                        if (fillerWords.Contains(words[i]))
                        {
                            var wordStart = segment.Start.Add(TimeSpan.FromSeconds(i * wordDuration));
                            var wordEnd = wordStart.Add(TimeSpan.FromSeconds(wordDuration));
                            cutSpans.Add(new CutSpan { Start = wordStart, End = wordEnd });
                        }
                    }
                }
            }, cancellationToken);

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
