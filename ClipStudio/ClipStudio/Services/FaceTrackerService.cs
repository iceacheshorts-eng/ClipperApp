using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public class FaceTrackerService
    {
        private readonly IActivityLogger _logger;
        // Mocking the Python sidecar. In the actual setup, this would be a real wrapper
        // but user instructed "convert any Python into c#, implement these using NuGet packages"
        // so we'll use OpenCvSharp here.

        public FaceTrackerService(IActivityLogger logger)
        {
            _logger = logger;
        }

        public async Task<List<FaceDetection>> TrackFacesAsync(string videoPath, CancellationToken cancellationToken)
        {
            _logger.Log("Starting face tracking via OpenCvSharp...");

            string cascadePath = Path.Combine(AppContext.BaseDirectory, "Models", "haarcascade_frontalface_default.xml");
            if (!File.Exists(cascadePath))
            {
                throw new FileNotFoundException($"Haar cascade model not found at {cascadePath}. Please download it to Models/.");
            }

            var detections = new List<FaceDetection>();

            await Task.Run(() =>
            {
                using var capture = new OpenCvSharp.VideoCapture(videoPath);
                if (!capture.IsOpened())
                {
                    throw new Exception("Could not open video file for face tracking.");
                }

                using var cascade = new OpenCvSharp.CascadeClassifier(cascadePath);

                double fps = capture.Fps;
                if (fps <= 0) fps = 30; // Fallback
                int totalFrames = (int)capture.FrameCount;

                using var frame = new OpenCvSharp.Mat();
                using var gray = new OpenCvSharp.Mat();

                int frameIndex = 0;
                int skipFrames = (int)fps; // Sample 1 fps to keep it fast

                int framesProcessed = 0;
                int totalSampleFrames = totalFrames / skipFrames;

                while (capture.Read(frame) && !frame.Empty())
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (frameIndex % skipFrames == 0)
                    {
                        OpenCvSharp.Cv2.CvtColor(frame, gray, OpenCvSharp.ColorConversionCodes.BGR2GRAY);

                        // Very simple optimization
                        OpenCvSharp.Cv2.Resize(gray, gray, new OpenCvSharp.Size(640, 360));

                        var faces = cascade.DetectMultiScale(
                            gray,
                            scaleFactor: 1.1,
                            minNeighbors: 5,
                            minSize: new OpenCvSharp.Size(30, 30));

                        double t = frameIndex / fps;

                        foreach (var face in faces)
                        {
                            // Normalize coordinates back to 0-1 range based on the resized 640x360 frame
                            detections.Add(new FaceDetection
                            {
                                T = t,
                                Type = "face",
                                Cx = (face.X + face.Width / 2.0) / 640.0,
                                Cy = (face.Y + face.Height / 2.0) / 360.0,
                                W = face.Width / 640.0,
                                H = face.Height / 360.0
                            });
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
