using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public class VideoAnalyzer
    {
        private readonly IActivityLogger _logger;
        private readonly string _ffmpegPath;
        private readonly string _ffprobePath;

        public VideoAnalyzer(IActivityLogger logger)
        {
            _logger = logger;
            _ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Binaries", "ffmpeg.exe");
            _ffprobePath = Path.Combine(AppContext.BaseDirectory, "Binaries", "ffprobe.exe");
        }

        public async Task<string> ExtractAudioAsync(string videoPath, CancellationToken cancellationToken)
        {
            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException($"ffmpeg.exe not found at {_ffmpegPath}");

            string wavPath = TempPaths.NewTempFile(".wav");

            _logger.Log("Extracting audio to WAV...");

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _ffmpegPath,
                WorkingDirectory = TempPaths.GetTempDir()
            };
            startInfo.ArgumentList.Add("-y");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(videoPath);
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-acodec");
            startInfo.ArgumentList.Add("pcm_s16le");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("16000");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add(wavPath);

            int exitCode = await ProcessUtils.RunProcessAsync(startInfo, line => {
                // optional: log ffmpeg output
            }, cancellationToken);

            if (exitCode != 0)
                throw new Exception($"Audio extraction failed with exit code {exitCode}");

            return wavPath;
        }

        public async Task<List<double>> AnalyzeAudioLoudnessAsync(string wavPath, CancellationToken cancellationToken)
        {
            return await Task.Run(() =>
            {
                _logger.Log("Analyzing audio loudness...");
                var loudnessScores = new List<double>();
                using var reader = new AudioFileReader(wavPath);

                // Sample every 1 second (16000 samples for 16kHz)
                int samplesPerSecond = reader.WaveFormat.SampleRate;
                float[] buffer = new float[samplesPerSecond];
                int bytesRead;

                while ((bytesRead = reader.Read(buffer, 0, buffer.Length)) > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    double sumSquares = 0;
                    for (int i = 0; i < bytesRead; i++)
                    {
                        sumSquares += buffer[i] * buffer[i];
                    }
                    double rms = Math.Sqrt(sumSquares / bytesRead);

                    // Normalize to 0-1 range (RMS usually 0 to ~0.5 for normalized audio, but can vary)
                    double score = Math.Min(1.0, rms * 5.0); // Simple scaling
                    loudnessScores.Add(score);
                }

                return loudnessScores;
            }, cancellationToken);
        }

        private const double AutoMinSeconds = 15.0;
        private const double AutoMaxSeconds = 60.0;
        private const double AutoStepSeconds = 5.0;
        private const double HookSeconds = 3.0;
        private const double HookWeight = 0.3;

        // Allowed lengths for auto fallback
        private static readonly int[] AutoCandidateLengths = { 15, 20, 30, 45, 60 };

        public List<ClipCandidate> GenerateAutoLengthCandidates(List<double> loudnessScores, List<FaceDetection> detections, ContentStyle style, int clipCount)
        {
            if (loudnessScores.Count < AutoMinSeconds)
                return new List<ClipCandidate>();

            _logger.Log($"Generating {clipCount} clips based on '{style}' auto-length heuristics...");
            var candidates = new List<ClipCandidate>();

            // Pre-calculate prefix sums for loudness
            double[] loudnessPrefixSum = new double[loudnessScores.Count + 1];
            for (int i = 0; i < loudnessScores.Count; i++)
            {
                loudnessPrefixSum[i + 1] = loudnessPrefixSum[i] + loudnessScores[i];
            }

            // Pre-calculate face counts per second
            int[] faceCountsPerSecond = new int[loudnessScores.Count];
            foreach (var d in detections)
            {
                int sec = (int)Math.Floor(d.T);
                if (sec >= 0 && sec < faceCountsPerSecond.Length)
                {
                    faceCountsPerSecond[sec]++;
                }
            }

            // Prefix sums for face counts
            int[] facesPrefixSum = new int[faceCountsPerSecond.Length + 1];
            for (int i = 0; i < faceCountsPerSecond.Length; i++)
            {
                facesPrefixSum[i + 1] = facesPrefixSum[i] + faceCountsPerSecond[i];
            }

            var potentialWindows = new List<(int StartIndex, int Length, double FinalScore)>();

            for (int i = 0; i < loudnessScores.Count; i += (int)AutoStepSeconds)
            {
                foreach (int L in AutoCandidateLengths)
                {
                    if (i + L <= loudnessScores.Count)
                    {
                        double loudnessSum = loudnessPrefixSum[i + L] - loudnessPrefixSum[i];
                        double avgLoudness = loudnessSum / L;

                        int facesInWindow = facesPrefixSum[i + L] - facesPrefixSum[i];
                        double faceScore = Math.Min(1.0, facesInWindow / (L * 5.0));

                        double baseScore = avgLoudness;
                        switch (style)
                        {
                            case ContentStyle.Podcast:
                                baseScore = (avgLoudness * 0.4) + (faceScore * 0.6);
                                break;
                            case ContentStyle.HighEnergy:
                                baseScore = (avgLoudness * 0.8) + (faceScore * 0.2);
                                break;
                            default:
                                baseScore = (avgLoudness * 0.5) + (faceScore * 0.5);
                                break;
                        }

                        // Calculate hook over first HookSeconds
                        double hookSum = loudnessPrefixSum[Math.Min(loudnessScores.Count, i + (int)HookSeconds)] - loudnessPrefixSum[i];
                        double hook = hookSum / HookSeconds; // We assume there's always at least HookSeconds remaining if L >= 15

                        double finalScore = (1 - HookWeight) * baseScore + HookWeight * hook;

                        potentialWindows.Add((i, L, finalScore));
                    }
                }
            }

            var sortedWindows = potentialWindows.OrderByDescending(w => w.FinalScore).ToList();

            foreach (var window in sortedWindows)
            {
                if (candidates.Count >= clipCount) break;

                bool overlaps = candidates.Any(c =>
                    !(window.StartIndex + window.Length <= c.StartTime.TotalSeconds ||
                      window.StartIndex >= c.EndTime.TotalSeconds));

                if (!overlaps)
                {
                    candidates.Add(new ClipCandidate
                    {
                        StartTime = TimeSpan.FromSeconds(window.StartIndex),
                        EndTime = TimeSpan.FromSeconds(window.StartIndex + window.Length),
                        Score = window.FinalScore
                    });
                }
            }

            candidates = candidates.OrderBy(c => c.StartTime).ToList();

            if (candidates.Any())
            {
                var summary = string.Join(", ", candidates.Select(c => $"{c.StartTime:hh\\:mm\\:ss} ({(c.EndTime - c.StartTime).TotalSeconds}s)"));
                _logger.Log($"Auto-length fallback picked: {summary}");
            }

            return candidates;
        }

        public List<ClipCandidate> GenerateCandidates(List<double> loudnessScores, List<FaceDetection> detections, ContentStyle style, int clipCount, double clipLength)
        {
            _logger.Log($"Generating {clipCount} clips based on '{style}' heuristics...");
            var candidates = new List<ClipCandidate>();

            int windowSize = (int)Math.Max(1, clipLength);
            var potentialWindows = new List<(int StartIndex, double AvgScore)>();

            for (int i = 0; i <= loudnessScores.Count - windowSize; i += 5) // Step by 5 seconds
            {
                double loudnessSum = 0;
                for (int j = 0; j < windowSize; j++)
                {
                    loudnessSum += loudnessScores[i + j];
                }
                double avgLoudness = loudnessSum / windowSize;

                // Factor in face presence
                // Check if faces exist in this window
                var facesInWindow = detections.Count(d => d.T >= i && d.T <= i + windowSize);
                // Assume 30fps track, so windowSize * 30 max faces. Normalizing this roughly.
                double faceScore = Math.Min(1.0, facesInWindow / (windowSize * 5.0));

                double finalScore = avgLoudness;

                // Adjust scores based on style
                switch (style)
                {
                    case ContentStyle.Podcast:
                        // Heavily weight face presence (talking head)
                        finalScore = (avgLoudness * 0.4) + (faceScore * 0.6);
                        break;
                    case ContentStyle.HighEnergy:
                        // Heavily weight loudness (shouting, action)
                        finalScore = (avgLoudness * 0.8) + (faceScore * 0.2);
                        break;
                    default:
                        // Balanced
                        finalScore = (avgLoudness * 0.5) + (faceScore * 0.5);
                        break;
                }

                potentialWindows.Add((i, finalScore));
            }

            var sortedWindows = potentialWindows.OrderByDescending(w => w.AvgScore).ToList();

            foreach (var window in sortedWindows)
            {
                if (candidates.Count >= clipCount) break;

                bool overlaps = candidates.Any(c =>
                    !(window.StartIndex + windowSize <= c.StartTime.TotalSeconds ||
                      window.StartIndex >= c.EndTime.TotalSeconds));

                if (!overlaps)
                {
                    candidates.Add(new ClipCandidate
                    {
                        StartTime = TimeSpan.FromSeconds(window.StartIndex),
                        EndTime = TimeSpan.FromSeconds(window.StartIndex + windowSize),
                        Score = window.AvgScore
                    });
                }
            }

            return candidates.OrderBy(c => c.StartTime).ToList();
        }
    }
}
