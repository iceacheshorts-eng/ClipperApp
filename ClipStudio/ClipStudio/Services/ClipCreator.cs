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
        private const double BlurSigma = 6.0;
        private const double BlurDimFactor = 0.55;
        private const double BlurDownscaleDivisor = 10.0;

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
            CancellationToken cancellationToken,
            IReadOnlyList<WordTiming>? words = null,
            CaptionStyle? captionStyle = null)
        {
            if (!File.Exists(_ffmpegPath))
                throw new FileNotFoundException($"ffmpeg.exe not found at {_ffmpegPath}");

            _logger.Log($"Rendering clip [{clip.StartTime:hh\\:mm\\:ss} - {clip.EndTime:hh\\:mm\\:ss}] with advanced dynamic frame cropping to {Path.GetFileName(outputFilePath)}");

            string workDir = TempPaths.GetTempDir();

            // Temporary files for the 2-step process
            string tempFullVideoPath = TempPaths.NewTempFile(".mp4");
            string? tempAssPath = null;

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

                // Pre-calculate captions and filter strings outside Task.Run
                List<(double Start, double End)>? clipKeepSpans = null;
                var clipFillers = fillerWords?
                    .Where(f => f.Start < clip.EndTime && f.End > clip.StartTime)
                    .ToList();

                if (clipFillers != null && clipFillers.Any())
                {
                    clipKeepSpans = BuildKeepSpans(clip, clipFillers);
                }

                string assFilterString = "";
                if (words != null && captionStyle != null)
                {
                    bool hasAss = await FfmpegCapabilities.HasFilterAsync(_ffmpegPath, "ass", cancellationToken);
                    if (hasAss)
                    {
                        var mappedWords = CaptionAssBuilder.MapWordsToOutputTimeline(words, clip.StartTime.TotalSeconds, clip.EndTime.TotalSeconds, clipKeepSpans);
                        if (mappedWords.Count > 0)
                        {
                            tempAssPath = TempPaths.NewTempFile(".ass");
                            string assContent = CaptionAssBuilder.Build(mappedWords, captionStyle, CropMath.OutputWidth, CropMath.OutputHeight);
                            await File.WriteAllTextAsync(tempAssPath, assContent, new UTF8Encoding(false), cancellationToken);

                            string assFileName = Path.GetFileName(tempAssPath);
                            string systemFontsDir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts).Replace('\\', '/').Replace(":", "\\:");
                            assFilterString = $",ass=filename={assFileName}:fontsdir='{systemFontsDir}'";
                        }
                        else
                        {
                            _logger.Log("Captions skipped: no words map to the output timeline.");
                        }
                    }
                    else
                    {
                        _logger.Log("Captions skipped: ffmpeg 'ass' filter is not available.");
                    }
                }

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
                    pipeStartInfo.ArgumentList.Add($"{CropMath.OutputWidth}x{CropMath.OutputHeight}");
                    pipeStartInfo.ArgumentList.Add("-r");
                    pipeStartInfo.ArgumentList.Add(fps.ToString("R", CultureInfo.InvariantCulture));
                    pipeStartInfo.ArgumentList.Add("-pix_fmt");
                    pipeStartInfo.ArgumentList.Add("bgr24");
                    pipeStartInfo.ArgumentList.Add("-i");
                    pipeStartInfo.ArgumentList.Add("-");
                    pipeStartInfo.ArgumentList.Add("-i");
                    pipeStartInfo.ArgumentList.Add(tempFullVideoPath);

                    bool hasFilterComplex = false;
                    if (clipKeepSpans != null && clipFillers != null && clipFillers.Any())
                    {
                        var selectExpr = BuildSelectExpression(clip, clipKeepSpans);
                        pipeStartInfo.ArgumentList.Add("-filter_complex");
                        pipeStartInfo.ArgumentList.Add($"[0:v]select='{selectExpr}',setpts=N/FRAME_RATE/TB,scale=1080:1920:flags=lanczos,format=yuv420p{assFilterString}[vout];[1:a]aselect='{selectExpr}',asetpts=N/SR/TB[aout]");
                        pipeStartInfo.ArgumentList.Add("-map");
                        pipeStartInfo.ArgumentList.Add("[vout]");
                        pipeStartInfo.ArgumentList.Add("-map");
                        pipeStartInfo.ArgumentList.Add("[aout]");
                        hasFilterComplex = true;
                    }

                    if (!hasFilterComplex)
                    {
                        pipeStartInfo.ArgumentList.Add("-vf");
                        pipeStartInfo.ArgumentList.Add($"scale=1080:1920:flags=lanczos,format=yuv420p{assFilterString}");
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

                        int outBytesCount = CropMath.OutputWidth * CropMath.OutputHeight * 3;
                        byte[] frameBytes = new byte[outBytesCount];

                        using var outFrame = new OpenCvSharp.Mat(CropMath.OutputHeight, CropMath.OutputWidth, OpenCvSharp.MatType.CV_8UC3);
                        using var panelTemp = new OpenCvSharp.Mat();
                        using var blurBackgroundTemp = new OpenCvSharp.Mat();
                        using var blurForegroundTemp = new OpenCvSharp.Mat();

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

                            CropLayout layout = cropPoint?.Layout ?? CropLayout.Single;

                            if (layout == CropLayout.Single)
                            {
                                ComposeSingle(frame, outFrame, cropPoint, width, height, targetWidth, targetHeight);
                            }
                            else if (layout == CropLayout.Stacked)
                            {
                                ComposeStacked(frame, outFrame, panelTemp, blurBackgroundTemp, blurForegroundTemp, cropPoint, width, height, targetWidth, targetHeight);
                            }
                            else
                            {
                                ComposeBlurFit(frame, outFrame, blurBackgroundTemp, blurForegroundTemp, width, height, targetWidth, targetHeight);
                            }

                            System.Runtime.InteropServices.Marshal.Copy(outFrame.Data, frameBytes, 0, frameBytes.Length);

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

                if (tempAssPath != null && File.Exists(tempAssPath))
                {
                    try { File.Delete(tempAssPath); } catch { }
                }
            }
        }

        private static void ComposeSingle(OpenCvSharp.Mat frame, OpenCvSharp.Mat outFrame, CropTrackBuilder.CropPoint? cropPoint, int width, int height, int targetWidth, int targetHeight)
        {
            double cx = cropPoint?.Cx ?? 0.5;
            int x = (int)(cx * width - targetWidth / 2.0);
            x = Math.Clamp(x, 0, width - targetWidth);
            var rect = new OpenCvSharp.Rect(x, 0, targetWidth, targetHeight);
            using var crop = new OpenCvSharp.Mat(frame, rect);
            OpenCvSharp.Cv2.Resize(crop, outFrame, new OpenCvSharp.Size(CropMath.OutputWidth, CropMath.OutputHeight), 0, 0, OpenCvSharp.InterpolationFlags.Lanczos4);
        }

        private static void ComposeStacked(OpenCvSharp.Mat frame, OpenCvSharp.Mat outFrame, OpenCvSharp.Mat panelTemp, OpenCvSharp.Mat blurBgTemp, OpenCvSharp.Mat blurFgTemp, CropTrackBuilder.CropPoint? cropPoint, int width, int height, int targetWidth, int targetHeight)
        {
            int regionW = CropMath.StackedRegionWidth(height);
            if (regionW > width)
            {
                ComposeBlurFit(frame, outFrame, blurBgTemp, blurFgTemp, width, height, targetWidth, targetHeight);
                return;
            }

            double cx1 = cropPoint?.Cx ?? 0.5;
            double cx2 = cropPoint?.Cx2 ?? 0.5;

            // Top panel
            int x1 = (int)Math.Round(cx1 * width - regionW / 2.0);
            x1 = Math.Clamp(x1, 0, width - regionW);
            var rect1 = new OpenCvSharp.Rect(x1, 0, regionW, height);
            using var crop1 = new OpenCvSharp.Mat(frame, rect1);
            OpenCvSharp.Cv2.Resize(crop1, panelTemp, new OpenCvSharp.Size(CropMath.OutputWidth, CropMath.PanelHeight), 0, 0, OpenCvSharp.InterpolationFlags.Area);
            var outRect1 = new OpenCvSharp.Rect(0, 0, CropMath.OutputWidth, CropMath.PanelHeight);
            using var outRoi1 = new OpenCvSharp.Mat(outFrame, outRect1);
            panelTemp.CopyTo(outRoi1);

            // Bottom panel
            int x2 = (int)Math.Round(cx2 * width - regionW / 2.0);
            x2 = Math.Clamp(x2, 0, width - regionW);
            var rect2 = new OpenCvSharp.Rect(x2, 0, regionW, height);
            using var crop2 = new OpenCvSharp.Mat(frame, rect2);
            OpenCvSharp.Cv2.Resize(crop2, panelTemp, new OpenCvSharp.Size(CropMath.OutputWidth, CropMath.PanelHeight), 0, 0, OpenCvSharp.InterpolationFlags.Area);
            var outRect2 = new OpenCvSharp.Rect(0, CropMath.PanelHeight, CropMath.OutputWidth, CropMath.PanelHeight);
            using var outRoi2 = new OpenCvSharp.Mat(outFrame, outRect2);
            panelTemp.CopyTo(outRoi2);
        }

        private static void ComposeBlurFit(OpenCvSharp.Mat frame, OpenCvSharp.Mat outFrame, OpenCvSharp.Mat blurBgTemp, OpenCvSharp.Mat blurFgTemp, int width, int height, int targetWidth, int targetHeight)
        {
            int fgH = (int)Math.Round(height * CropMath.OutputWidth / (double)width);
            if (fgH > CropMath.OutputHeight)
            {
                ComposeSingle(frame, outFrame, null, width, height, targetWidth, targetHeight);
                return;
            }

            // Background blur
            int bgX = (width - targetWidth) / 2;
            var bgRect = new OpenCvSharp.Rect(bgX, 0, targetWidth, height);
            using var bgCrop = new OpenCvSharp.Mat(frame, bgRect);

            int blurW = (int)(CropMath.OutputWidth / BlurDownscaleDivisor);
            int blurH = (int)(CropMath.OutputHeight / BlurDownscaleDivisor);

            OpenCvSharp.Cv2.Resize(bgCrop, blurBgTemp, new OpenCvSharp.Size(blurW, blurH), 0, 0, OpenCvSharp.InterpolationFlags.Area);
            OpenCvSharp.Cv2.GaussianBlur(blurBgTemp, blurBgTemp, new OpenCvSharp.Size(0, 0), BlurSigma);
            OpenCvSharp.Cv2.Resize(blurBgTemp, outFrame, new OpenCvSharp.Size(CropMath.OutputWidth, CropMath.OutputHeight), 0, 0, OpenCvSharp.InterpolationFlags.Linear);
            OpenCvSharp.Cv2.ConvertScaleAbs(outFrame, outFrame, BlurDimFactor, 0);

            // Foreground center
            OpenCvSharp.Cv2.Resize(frame, blurFgTemp, new OpenCvSharp.Size(CropMath.OutputWidth, fgH), 0, 0, OpenCvSharp.InterpolationFlags.Area);
            int y = (CropMath.OutputHeight - fgH) / 2;
            var fgRect = new OpenCvSharp.Rect(0, y, CropMath.OutputWidth, fgH);
            using var outRoi = new OpenCvSharp.Mat(outFrame, fgRect);
            blurFgTemp.CopyTo(outRoi);
        }

        private static List<(double Start, double End)> BuildKeepSpans(ClipCandidate clip, List<TranscriptionService.CutSpan> fillers)
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

            return keepSpans;
        }

        private string BuildSelectExpression(ClipCandidate clip, List<(double Start, double End)> keepSpans)
        {
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
