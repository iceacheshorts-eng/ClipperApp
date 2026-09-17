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

        public async Task<List<FaceDetection>> TrackFacesAsync(string videoPath, CancellationToken cancellationToken)
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

                int frameIndex = 0;

                // Sample 5 frames per second for high accuracy and smooth tracking
                double targetFps = 5.0;
                int skipFrames = Math.Max(1, (int)Math.Round(fps / targetFps));

                int framesProcessed = 0;
                int totalSampleFrames = totalFrames / skipFrames;

                while (capture.Read(frame) && !frame.Empty())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (frameIndex % skipFrames == 0)
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
                            _logger.Log($"Face Tracking: Processed {framesProcessed} out of {totalSampleFrames} frames...");
                        }
                    }
                    frameIndex++;
                }

            }, cancellationToken);

            _logger.Log($"Face tracking completed. Found {detections.Count} detection points.");
            return detections;
        }
    }
}
