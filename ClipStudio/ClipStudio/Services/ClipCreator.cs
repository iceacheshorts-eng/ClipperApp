using System;
using System.Collections.Generic;
using System.Globalization;
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
            List<TranscriptionService.CutSpan>? fillerWords,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException($"ffmpeg.exe not found at {_ffmpegPath}");

            _logger.Log($"Rendering clip [{clip.StartTime:hh\\:mm\\:ss} - {clip.EndTime:hh\\:mm\\:ss}] with advanced dynamic frame cropping to {Path.GetFileName(outputFilePath)}");

            string workDir = TempPaths.GetTempDir();

            // Temporary files for the 2-step process
            string tempFullVideoPath = TempPaths.NewTempFile(".mp4");

            try
            {
                // STEP 1: Fast extract the un-cropped continuous clip segment with audio
                // This ensures we have perfectly synced audio and a small file to process frame-by-frame.
                // We do NOT filter filler words yet, so the timeline stays 1:1 with the crop track.

                var extractStartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = _ffmpegPath,
                    WorkingDirectory = workDir
                };
                extractStartInfo.ArgumentList.Add("-y");
                extractStartInfo.ArgumentList.Add("-ss");
                extractStartInfo.ArgumentList.Add(clip.StartTime.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                extractStartInfo.ArgumentList.Add("-to");
                extractStartInfo.ArgumentList.Add(clip.EndTime.TotalSeconds.ToString(CultureInfo.InvariantCulture));
                extractStartInfo.ArgumentList.Add("-i");
                extractStartInfo.ArgumentList.Add(sourceVideoPath);
                extractStartInfo.ArgumentList.Add("-c:v");
                extractStartInfo.ArgumentList.Add("libx264");
                extractStartInfo.ArgumentList.Add("-preset");
                extractStartInfo.ArgumentList.Add("ultrafast");
                extractStartInfo.ArgumentList.Add("-crf");
                extractStartInfo.ArgumentList.Add("18");
                extractStartInfo.ArgumentList.Add("-c:a");
                extractStartInfo.ArgumentList.Add("aac");
                extractStartInfo.ArgumentList.Add("-b:a");
                extractStartInfo.ArgumentList.Add("192k");
                extractStartInfo.ArgumentList.Add(tempFullVideoPath);

                int extractExitCode = await ProcessUtils.RunProcessAsync(extractStartInfo, _ => {}, cancellationToken);
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
                    int targetWidth = CropMath.TargetWidth(height);
                    // Ensure targetWidth doesn't exceed frame width
                    targetWidth = Math.Min(targetWidth, width - width % 2);

                    var pipeStartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = _ffmpegPath,
                        WorkingDirectory = workDir,
                        RedirectStandardInput = true,
                        RedirectStandardOutput = false,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    };

                    pipeStartInfo.ArgumentList.Add("-y");
                    pipeStartInfo.ArgumentList.Add("-loglevel");
                    pipeStartInfo.ArgumentList.Add("error");
                    pipeStartInfo.ArgumentList.Add("-f");
                    pipeStartInfo.ArgumentList.Add("rawvideo");
                    pipeStartInfo.ArgumentList.Add("-vcodec");
                    pipeStartInfo.ArgumentList.Add("rawvideo");
                    pipeStartInfo.ArgumentList.Add("-s");
                    pipeStartInfo.ArgumentList.Add($"{targetWidth}x{targetHeight}");
                    pipeStartInfo.ArgumentList.Add("-r");
                    pipeStartInfo.ArgumentList.Add(fps.ToString("R", CultureInfo.InvariantCulture));
                    pipeStartInfo.ArgumentList.Add("-pix_fmt");
                    pipeStartInfo.ArgumentList.Add("bgr24");
                    pipeStartInfo.ArgumentList.Add("-i");
                    pipeStartInfo.ArgumentList.Add("-");
                    pipeStartInfo.ArgumentList.Add("-i");
                    pipeStartInfo.ArgumentList.Add(tempFullVideoPath);

                    bool hasFilterComplex = false;
                    if (fillerWords != null && fillerWords.Any())
                    {
                        var clipFillers = fillerWords
                            .Where(f => f.Start < clip.EndTime && f.End > clip.StartTime)
                            .ToList();

                        if (clipFillers.Any())
                        {
                            var selectExpr = BuildSelectExpression(clip, clipFillers);
                            pipeStartInfo.ArgumentList.Add("-filter_complex");
                            pipeStartInfo.ArgumentList.Add($"[0:v]select='{selectExpr}',setpts=N/FRAME_RATE/TB,scale=1080:1920:flags=lanczos,format=yuv420p[vout];[1:a]aselect='{selectExpr}',asetpts=N/SR/TB[aout]");
                            pipeStartInfo.ArgumentList.Add("-map");
                            pipeStartInfo.ArgumentList.Add("[vout]");
                            pipeStartInfo.ArgumentList.Add("-map");
                            pipeStartInfo.ArgumentList.Add("[aout]");
                            hasFilterComplex = true;
                        }
                    }

                    if (!hasFilterComplex)
                    {
                        pipeStartInfo.ArgumentList.Add("-vf");
                        pipeStartInfo.ArgumentList.Add("scale=1080:1920:flags=lanczos,format=yuv420p");
                        pipeStartInfo.ArgumentList.Add("-map");
                        pipeStartInfo.ArgumentList.Add("0:v:0");
                        pipeStartInfo.ArgumentList.Add("-map");
                        pipeStartInfo.ArgumentList.Add("1:a:0?");
                    }

                    pipeStartInfo.ArgumentList.Add("-c:v");
                    pipeStartInfo.ArgumentList.Add("libx264");
                    pipeStartInfo.ArgumentList.Add("-preset");
                    pipeStartInfo.ArgumentList.Add("fast");
                    pipeStartInfo.ArgumentList.Add("-crf");
                    pipeStartInfo.ArgumentList.Add("23");
                    pipeStartInfo.ArgumentList.Add("-pix_fmt");
                    pipeStartInfo.ArgumentList.Add("yuv420p");
                    pipeStartInfo.ArgumentList.Add("-movflags");
                    pipeStartInfo.ArgumentList.Add("+faststart");
                    pipeStartInfo.ArgumentList.Add("-c:a");
                    pipeStartInfo.ArgumentList.Add("aac");
                    pipeStartInfo.ArgumentList.Add("-b:a");
                    pipeStartInfo.ArgumentList.Add("128k");
                    pipeStartInfo.ArgumentList.Add(outputFilePath);

                    bool succeeded = false;
                    System.Diagnostics.Process? process = null;

                    try
                    {
                        process = new System.Diagnostics.Process { StartInfo = pipeStartInfo };
                        process.Start();

                        // Asynchronously consume standard error to avoid process deadlock
                        var errorLogTask = process.StandardError.ReadToEndAsync();

                        // Pre-filter the crop track for this clip to optimize lookup
                        var clipTrack = cropTrack.Where(p => p.T >= clip.StartTime.TotalSeconds && p.T <= clip.EndTime.TotalSeconds).OrderBy(p => p.T).ToList();

                        using var frame = new OpenCvSharp.Mat();
                        int frameIndex = 0;
                        int trackIndex = 0;

                        byte[]? frameBytes = null;

                        while (capture.Read(frame) && !frame.Empty())
                        {
                            cancellationToken.ThrowIfCancellationRequested();

                            double currentVideoTime = frameIndex / fps;
                            double absoluteTime = clip.StartTime.TotalSeconds + currentVideoTime;

                            // Advance track index directly instead of OrderBy
                            while (trackIndex < clipTrack.Count - 1 && clipTrack[trackIndex + 1].T <= absoluteTime)
                            {
                                trackIndex++;
                            }

                            var cropPoint = clipTrack.Count > 0 ? clipTrack[trackIndex] : null;

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

                            int byteSize = (int)(croppedFrame.Total() * croppedFrame.ElemSize());
                            if (frameBytes == null || frameBytes.Length != byteSize)
                            {
                                frameBytes = new byte[byteSize];
                            }
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
                        try
                        {
                            process.StandardInput.Close();
                        }
                        catch (IOException)
                        {
                            // Ignored
                        }
                        process.WaitForExit();

                        if (process.ExitCode != 0)
                        {
                            string errorLog = errorLogTask.Result;
                            throw new Exception($"ffmpeg piping failed with exit code {process.ExitCode}. Error: {errorLog}");
                        }

                        succeeded = true;
                    }
                    finally
                    {
                        if (!succeeded)
                        {
                            try { if (process is { HasExited: false }) { process.Kill(entireProcessTree: true); process.WaitForExit(3000); } } catch { }
                            try { if (File.Exists(outputFilePath)) File.Delete(outputFilePath); } catch { }
                        }

                        if (process != null)
                        {
                            process.Dispose();
                        }
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

        private string BuildSelectExpression(ClipCandidate clip, List<TranscriptionService.CutSpan> fillers)
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
            {
                double start = s.Start - clip.StartTime.TotalSeconds;
                double end = s.End - clip.StartTime.TotalSeconds;
                return FormattableString.Invariant($"(t>={start:F3}*t<{end:F3})");
            });

            return string.Join("+", terms); // plus means OR in ffmpeg select expression
        }
    }
}
