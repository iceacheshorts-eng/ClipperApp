using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ClipStudio.Models;
using OpenCvSharp.Dnn;
using OpenCvSharp;

namespace ClipStudio.Services
{
    public class FaceTrackerService
    {
        private const double SeekThresholdSeconds = 8.0;

        private readonly IActivityLogger _logger;

        public FaceTrackerService(IActivityLogger logger)
        {
            _logger = logger;
        }

        private async Task EnsureModelExistsAsync(string modelsDir, string prototxtPath, string caffemodelPath)
        {
            if (!Directory.Exists(modelsDir))
            {
                Directory.CreateDirectory(modelsDir);
            }

            using var httpClient = new HttpClient();

            if (!File.Exists(prototxtPath))
            {
                _logger.Log("Downloading ResNet DNN prototxt...");
                string url = "https://raw.githubusercontent.com/opencv/opencv/master/samples/dnn/face_detector/deploy.prototxt";
                var bytes = await httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(prototxtPath, bytes);
            }

            if (!File.Exists(caffemodelPath))
            {
                _logger.Log("Downloading ResNet DNN caffemodel (this may take a moment)...");
                string url = "https://raw.githubusercontent.com/opencv/opencv_3rdparty/dnn_samples_face_detector_20170830/res10_300x300_ssd_iter_140000.caffemodel";
                var bytes = await httpClient.GetByteArrayAsync(url);
                await File.WriteAllBytesAsync(caffemodelPath, bytes);
            }
        }

        public Task<List<FaceDetection>> TrackFacesAsync(string videoPath, CancellationToken ct)
        {
            return TrackFacesAsync(videoPath, null, ct);
        }

        public async Task<List<FaceDetection>> TrackFacesAsync(string videoPath, IReadOnlyList<(double Start, double End)>? ranges, CancellationToken ct)
        {
            _logger.Log("Starting advanced AI face tracking (SSD ResNet-10)...");

            string modelsDir = Path.Combine(AppContext.BaseDirectory, "Models");
            string prototxtPath = Path.Combine(modelsDir, "deploy.prototxt");
            string caffemodelPath = Path.Combine(modelsDir, "res10_300x300_ssd_iter_140000.caffemodel");

            await EnsureModelExistsAsync(modelsDir, prototxtPath, caffemodelPath);

            var detections = new List<FaceDetection>();

            await Task.Run(() =>
            {
                using var net = CvDnn.ReadNetFromCaffe(prototxtPath, caffemodelPath);

                using var capture = new VideoCapture(videoPath);
                if (!capture.IsOpened())
                {
                    throw new Exception("Could not open video file for face tracking.");
                }

                double fps = capture.Fps;
                if (fps <= 0) fps = 30; // Fallback
                int totalFrames = (int)capture.FrameCount;

                using var frame = new Mat();

                // Sample 5 frames per second for high accuracy and smooth tracking
                double targetFps = 5.0;
                int skipFrames = Math.Max(1, (int)Math.Round(fps / targetFps));

                // Normalize ranges
                var spans = new List<(int StartFrame, int EndFrame)>();
                if (ranges == null)
                {
                    spans.Add((0, int.MaxValue));
                }
                else
                {
                    var sorted = new List<(double Start, double End)>(ranges);
                    for (int i = 0; i < sorted.Count; i++)
                    {
                        sorted[i] = (Math.Max(0, sorted[i].Start), sorted[i].End);
                    }
                    sorted.Sort((a, b) => a.Start.CompareTo(b.Start));

                    var merged = new List<(double Start, double End)>();
                    foreach (var r in sorted)
                    {
                        if (merged.Count > 0)
                        {
                            var last = merged[merged.Count - 1];
                            if (r.Start - last.End <= 1.0)
                            {
                                merged[merged.Count - 1] = (last.Start, Math.Max(last.End, r.End));
                            }
                            else
                            {
                                merged.Add(r);
                            }
                        }
                        else
                        {
                            merged.Add(r);
                        }
                    }

                    foreach (var m in merged)
                    {
                        int sf = (int)Math.Floor(m.Start * fps);
                        int ef = m.End == double.MaxValue ? int.MaxValue : (int)Math.Ceiling(m.End * fps);
                        spans.Add((sf, ef));
                    }
                }

                long totalSampleFrames = 0;
                bool showTotal = totalFrames > 0;
                if (showTotal)
                {
                    long framesToProcess = 0;
                    if (ranges == null)
                    {
                        framesToProcess = totalFrames;
                    }
                    else
                    {
                        foreach (var span in spans)
                        {
                            int ef = Math.Min(span.EndFrame, totalFrames - 1);
                            framesToProcess += (ef - span.StartFrame + 1);
                        }
                    }
                    totalSampleFrames = framesToProcess / skipFrames;
                }

                int framesProcessed = 0;
                int frameIndex = 0;
                bool endOfVideo = false;

                foreach (var span in spans)
                {
                    if (endOfVideo) break;
                    if (span.EndFrame < frameIndex) continue;

                    if (span.StartFrame > frameIndex)
                    {
                        double gapSeconds = (span.StartFrame - frameIndex) / fps;
                        if (gapSeconds > SeekThresholdSeconds)
                        {
                            if (capture.Set(VideoCaptureProperties.PosFrames, span.StartFrame))
                            {
                                int landedFrame = (int)Math.Round(capture.Get(VideoCaptureProperties.PosFrames));
                                double diffSeconds = Math.Abs(span.StartFrame - landedFrame) / fps;
                                if (diffSeconds > 1.0)
                                {
                                    _logger.Log($"Warning: Seek to frame {span.StartFrame} landed at {landedFrame}");
                                }
                                else
                                {
                                    _logger.Log($"Seek to frame {span.StartFrame} landed at {landedFrame}");
                                }
                                frameIndex = landedFrame;
                            }
                        }

                        while (frameIndex < span.StartFrame)
                        {
                            ct.ThrowIfCancellationRequested();
                            if (!capture.Grab())
                            {
                                endOfVideo = true;
                                break;
                            }
                            frameIndex++;
                        }
                    }

                    if (endOfVideo) break;

                    while (frameIndex <= span.EndFrame)
                    {
                        ct.ThrowIfCancellationRequested();

                        if (!capture.Grab())
                        {
                            endOfVideo = true;
                            break;
                        }

                        if (frameIndex % skipFrames == 0)
                        {
                            if (capture.Retrieve(frame) && !frame.Empty())
                            {
                                // The SSD model expects 300x300 input blob
                                using var blob = CvDnn.BlobFromImage(frame, 1.0, new Size(300, 300), new Scalar(104.0, 177.0, 123.0), false, false);

                                net!.SetInput(blob, "data");
                                using var detection = net.Forward("detection_out");

                                // detection output is 4D: [1, 1, N, 7]
                                var detectionMat = Mat.FromPixelData(detection!.Size(2), detection!.Size(3), MatType.CV_32F, detection!.Data);

                                double t = frameIndex / fps;

                                int rows = detectionMat.Rows;
                                for (int i = 0; i < rows; i++)
                                {
                                    float confidence = detectionMat.At<float>(i, 2);

                                    // Strict confidence threshold for accuracy
                                    if (confidence > 0.5)
                                    {
                                        float x1 = detectionMat.At<float>(i, 3);
                                        float y1 = detectionMat.At<float>(i, 4);
                                        float x2 = detectionMat.At<float>(i, 5);
                                        float y2 = detectionMat.At<float>(i, 6);

                                        detections.Add(new FaceDetection
                                        {
                                            T = t,
                                            Type = "face",
                                            Cx = (x1 + x2) / 2.0,
                                            Cy = (y1 + y2) / 2.0,
                                            W = Math.Abs(x2 - x1),
                                            H = Math.Abs(y2 - y1)
                                        });
                                    }
                                }

                                framesProcessed++;
                                if (framesProcessed % 50 == 0)
                                {
                                    if (showTotal)
                                    {
                                        _logger.Log($"Face Tracking: Processed {framesProcessed} out of {totalSampleFrames} frames...");
                                    }
                                    else
                                    {
                                        _logger.Log($"Face Tracking: Processed {framesProcessed} frames...");
                                    }
                                }
                            }
                        }
                        frameIndex++;
                    }
                }

            }, ct);

            _logger.Log($"Face tracking completed. Found {detections.Count} detection points.");
            return detections;
        }
    }
}
