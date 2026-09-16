using System;
using System.Collections.Generic;
using System.Linq;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public class CropTrackBuilder
    {
        public class CropPoint
        {
            public double T { get; set; }
            public double Cx { get; set; }
            public double Cy { get; set; }
        }

        public List<CropPoint> BuildTrack(List<FaceDetection> detections, double videoDuration, double fps)
        {
            var track = new List<CropPoint>();
            if (detections == null || detections.Count == 0)
            {
                // Return default center track
                track.Add(new CropPoint { T = 0, Cx = 0.5, Cy = 0.5 });
                track.Add(new CropPoint { T = videoDuration, Cx = 0.5, Cy = 0.5 });
                return track;
            }

            double deadzone = 0.02;
            double easeFactor = 0.15;

            var orderedDetections = detections.OrderBy(d => d.T).ToList();

            // Assume 1 fps sampling for the input, we want to generate a point for every output frame (e.g. 30fps)
            // But to keep script size small, we can generate points every 0.1 seconds and let ffmpeg interpolate

            double currentT = 0;
            double currentCx = 0.5; // Start center
            double currentCy = 0.5;

            double targetCx = 0.5;
            double targetCy = 0.5;

            int detIndex = 0;

            while (currentT <= videoDuration)
            {
                // Update target if we have passed the next detection
                while (detIndex < orderedDetections.Count && orderedDetections[detIndex].T <= currentT)
                {
                    targetCx = orderedDetections[detIndex].Cx;
                    targetCy = orderedDetections[detIndex].Cy;
                    detIndex++;
                }

                // Check deadzone
                if (Math.Abs(targetCx - currentCx) > deadzone)
                {
                    currentCx += (targetCx - currentCx) * easeFactor;
                }

                if (Math.Abs(targetCy - currentCy) > deadzone)
                {
                    currentCy += (targetCy - currentCy) * easeFactor;
                }

                // Clamp to safe boundaries so 9:16 crop doesn't go out of bounds
                // Assuming output is 9:16 (w=0.5625 of height). So cx min/max needs clamping
                double cropW = 9.0 / 16.0;
                double minCx = cropW / 2.0;
                double maxCx = 1.0 - (cropW / 2.0);

                currentCx = Math.Clamp(currentCx, minCx, maxCx);
                // Vertical panning isn't usually needed as much for landscape->portrait, but clamped
                currentCy = Math.Clamp(currentCy, 0.5, 0.5);

                track.Add(new CropPoint
                {
                    T = currentT,
                    Cx = currentCx,
                    Cy = currentCy
                });

                currentT += 0.1; // 10 points per second
            }

            return track;
        }
    }
}
