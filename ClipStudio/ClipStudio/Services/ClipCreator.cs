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

            _logger.Log($"Rendering clip [{clip.StartTime:hh\\:mm\\:ss} - {clip.EndTime:hh\\:mm\\:ss}] with advanced dynamic frame cropping to {Path.GetFileName(outputFilePath)}");

            string workDir = Path.GetDirectoryName(sourceVideoPath) ?? "";

            // Temporary files for the 2-step process
            string tempFullVideoPath = Path.Combine(workDir, $"temp_full_{Guid.NewGuid()}.mp4");

            try
            {
                // STEP 1: Fast extract the un-cropped continuous clip segment with audio
                // This ensures we have perfectly synced audio and a small file to process frame-by-frame.
                // We do NOT filter filler words yet, so the timeline stays 1:1 with the crop track.
                string extractArgs = $"-y -ss {clip.StartTime.TotalSeconds} -to {clip.EndTime.TotalSeconds} -i \"{sourceVideoPath}\" " +
                                     $"-c:v libx264 -preset ultrafast -crf 18 -c:a aac -b:a 192k " +
                                     $"\"{tempFullVideoPath}\"";

                int extractExitCode = await ProcessUtils.RunProcessAsync(_ffmpegPath, extractArgs, workDir, _ => {}, cancellationToken);
                if (extractExitCode != 0) throw new Exception($"ffmpeg extraction failed with exit code {extractExitCode}");

                // STEP 2: Use OpenCvSharp to read the extracted video, apply smooth dynamic cropping frame-by-frame, and pipe to FFmpeg.
                await Task.Run(() =>
                {
                    using var capture = new OpenCvSharp.VideoCapture(tempFullVideoPath);
                    if (!capture.IsOpened()) throw new Exception("Could not open temp video for frame cropping.");

                    double fps = capture.Fps;
                    int width = capture.FrameWidth;
                    int height = capture.FrameHeight;

                    // Calculate target vertical crop dimensions
                    int targetHeight = height;
                    int targetWidth = (int)(height * 9.0 / 16.0);
                    // Ensure even dimensions
                    if (targetWidth % 2 != 0) targetWidth--;

                    // Build filter for filler words to apply to the piped output
                    string filterComplex = "";
                    string mapArgs = "-map 0:v:0 -map 1:a:0?";

                    if (fillerWords != null && fillerWords.Any())
                    {
                        var clipFillers = fillerWords
                            .Where(f => f.Start < clip.EndTime && f.End > clip.StartTime)
                            .ToList();

                        if (clipFillers.Any())
                        {
                            var selectExpr = BuildSelectExpression(clip, clipFillers);
                            filterComplex = $"-filter_complex \"[0:v]select='{selectExpr}',setpts=N/FRAME_RATE/TB[vout];[1:a]aselect='{selectExpr}',asetpts=N/SR/TB[aout]\" ";
                            mapArgs = "-map \"[vout]\" -map \"[aout]\"";
                        }
                    }

                    // Start an ffmpeg process that reads raw BGR24 frames from stdin
                    string pipeArgs = $"-y -loglevel error -f rawvideo -vcodec rawvideo -s {targetWidth}x{targetHeight} -r {fps} -pix_fmt bgr24 -i - " +
                                      $"-i \"{tempFullVideoPath}\" " + // input 1 is original for audio
                                      $"{filterComplex}{mapArgs} " +
                                      $"-c:v libx264 -preset fast -crf 23 -c:a aac -b:a 128k " +
                                      $"\"{outputFilePath}\"";

                    var processStartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        Arguments = pipeArgs,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workDir
                    };

                    using var process = new System.Diagnostics.Process { StartInfo = processStartInfo };
                    process.Start();

                    // Asynchronously consume standard error to avoid process deadlock
                    var errorLogTask = process.StandardError.ReadToEndAsync();

                    // Pre-filter the crop track for this clip to optimize lookup
                    var clipTrack = cropTrack.Where(p => p.T >= clip.StartTime.TotalSeconds && p.T <= clip.EndTime.TotalSeconds).ToList();

                    using var frame = new OpenCvSharp.Mat();
                    int frameIndex = 0;

                    while (capture.Read(frame) && !frame.Empty())
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        double currentVideoTime = frameIndex / fps;
                        double absoluteTime = clip.StartTime.TotalSeconds + currentVideoTime;

                        // Find the closest crop point
                        var cropPoint = clipTrack.OrderBy(p => Math.Abs(p.T - absoluteTime)).FirstOrDefault();

                        double cx = cropPoint?.Cx ?? 0.5;
                        double cy = cropPoint?.Cy ?? 0.5;

                        // Calculate crop rectangle safely
                        int x = (int)(cx * width - targetWidth / 2.0);
                        int y = (int)(cy * height - targetHeight / 2.0);

                        x = Math.Clamp(x, 0, width - targetWidth);
                        y = Math.Clamp(y, 0, height - targetHeight);

                        var cropRect = new OpenCvSharp.Rect(x, y, targetWidth, targetHeight);
                        using var subMat = new OpenCvSharp.Mat(frame, cropRect);
                        using var croppedFrame = subMat.Clone(); // Clone to guarantee contiguous memory stride

                        // Write raw bytes to ffmpeg stdin
                        byte[] frameBytes = new byte[croppedFrame.Total() * croppedFrame.ElemSize()];
                        System.Runtime.InteropServices.Marshal.Copy(croppedFrame.Data, frameBytes, 0, frameBytes.Length);

                        try
                        {
                            process.StandardInput.BaseStream.Write(frameBytes, 0, frameBytes.Length);
                        }
                        catch (IOException)
                        {
                            // ffmpeg process ended unexpectedly
                            break;
                        }

                        frameIndex++;
                    }

                    // Close stdin to tell ffmpeg we are done sending frames
                    process.StandardInput.Close();
                    process.WaitForExit();

                    if (process.ExitCode != 0)
                    {
                        string errorLog = errorLogTask.Result;
                        throw new Exception($"ffmpeg piping failed with exit code {process.ExitCode}. Error: {errorLog}");
                    }

                }, cancellationToken);

                _logger.Log($"Advanced dynamic clip rendered successfully: {outputFilePath}");
            }
            finally
            {
                if (File.Exists(tempFullVideoPath))
                {
                    try { File.Delete(tempFullVideoPath); } catch { }
                }
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
