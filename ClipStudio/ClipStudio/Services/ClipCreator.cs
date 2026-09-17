using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public class ClipCreator
    {
        private readonly IActivityLogger _logger;
        private readonly string _ffmpegPath;

        public ClipCreator(IActivityLogger logger)
        {
            _logger = logger;
            _ffmpegPath = Path.Combine(AppContext.BaseDirectory, "Binaries", "ffmpeg.exe");
        }

        public async Task RenderClipAsync(
            string sourceVideoPath,
            string outputFilePath,
            ClipCandidate clip,
            List<CropTrackBuilder.CropPoint> cropTrack,
            List<FillerWordDetectorService.CutSpan>? fillerWords,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException($"ffmpeg.exe not found at {_ffmpegPath}");

            _logger.Log($"Rendering clip [{clip.StartTime:hh\\:mm\\:ss} - {clip.EndTime:hh\\:mm\\:ss}] to {Path.GetFileName(outputFilePath)}");

            string workDir = Path.GetDirectoryName(sourceVideoPath) ?? "";

            try
            {
                // To avoid ffmpeg exit code -22 (Invalid argument), we will use a static average crop
                // for the clip instead of dynamic sendcmd which is often unsupported by the crop filter.

                // 1. Calculate average crop point for this clip duration
                double avgCx = 0.5;
                double avgCy = 0.5;

                var clipTrack = cropTrack.Where(p => p.T >= clip.StartTime.TotalSeconds && p.T <= clip.EndTime.TotalSeconds).ToList();
                if (clipTrack.Count > 0)
                {
                    avgCx = clipTrack.Average(p => p.Cx);
                    avgCy = clipTrack.Average(p => p.Cy);
                }

                // 2. Build filter graph
                // Target vertical 9:16. Let's assume input is 1080p (1920x1080) or 720p (1280x720).
                // Using 'ih*9/16' for width and 'ih' for height.
                // x = Cx * iw - (out_w / 2)
                // y = Cy * ih - (out_h / 2)
                string cropFilter = $"crop=w='ih*9/16':h='ih':x='{avgCx:F4}*iw - (ih*9/16)/2':y='{avgCy:F4}*ih - ih/2'";

                // If there are filler words inside this clip, we use select/aselect to cut them out
                string videoFilter = $"{cropFilter}";
                string audioFilter = "anull";

                if (fillerWords != null && fillerWords.Any())
                {
                    // Filter filler words that overlap with this clip
                    var clipFillers = fillerWords
                        .Where(f => f.Start < clip.EndTime && f.End > clip.StartTime)
                        .ToList();

                    if (clipFillers.Any())
                    {
                        var selectExpr = BuildSelectExpression(clip, clipFillers);
                        videoFilter += $",select='{selectExpr}',setpts=N/FRAME_RATE/TB";
                        audioFilter = $"aselect='{selectExpr}',asetpts=N/SR/TB";
                    }
                }

                string arguments = $"-y -ss {clip.StartTime.TotalSeconds} -to {clip.EndTime.TotalSeconds} -i \"{sourceVideoPath}\" " +
                                   $"-vf \"{videoFilter}\" " +
                                   $"-af \"{audioFilter}\" " +
                                   $"-c:v libx264 -preset fast -crf 23 -c:a aac -b:a 128k " +
                                   $"\"{outputFilePath}\"";

                int exitCode = await ProcessUtils.RunProcessAsync(_ffmpegPath, arguments, workDir, line => {
                    // Optional logging of ffmpeg rendering
                }, cancellationToken);

                if (exitCode != 0)
                {
                    throw new Exception($"ffmpeg render failed with exit code {exitCode}");
                }

                _logger.Log($"Clip rendered successfully: {outputFilePath}");
            }
            finally
            {
                // No cleanup needed for sendcmd as we removed it
            }
        }

        private string BuildSelectExpression(ClipCandidate clip, List<FillerWordDetectorService.CutSpan> fillers)
        {
            var keepSpans = new List<(double Start, double End)>();
            double current = clip.StartTime.TotalSeconds;

            foreach (var f in fillers.OrderBy(f => f.Start))
            {
                if (f.Start.TotalSeconds > current)
                {
                    keepSpans.Add((current, f.Start.TotalSeconds));
                }
                current = Math.Max(current, f.End.TotalSeconds);
            }

            if (current < clip.EndTime.TotalSeconds)
            {
                keepSpans.Add((current, clip.EndTime.TotalSeconds));
            }

            var terms = keepSpans.Select(s =>
                $"(t>={s.Start - clip.StartTime.TotalSeconds:F3}*t<{s.End - clip.StartTime.TotalSeconds:F3})"
            );

            return string.Join("+", terms).Replace("*", " * "); // plus means OR in ffmpeg select expression
        }
    }
}
