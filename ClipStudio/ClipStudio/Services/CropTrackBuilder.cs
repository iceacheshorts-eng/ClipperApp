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

            // Upgraded cinematic tracking parameters
            double deadzone = 0.05; // Slightly larger deadzone to avoid micro-jitters

            // Increased spring constant for a faster pan when target changes
            double springConstant = 25.0; // Stiffness of the camera "spring"
            double dampingRatio = 1.0; // Critically damped (no bouncing, just smooth arrival)
            double dt = 1.0 / fps; // output fps integration step
            double damping = 2.0 * Math.Sqrt(springConstant) * dampingRatio;

            // Group detections by time (T). For multiple faces at the same timestamp, pick the largest one (nearest person).
            var orderedDetections = detections
                .GroupBy(d => d.T)
                .Select(g => g.OrderByDescending(d => d.W * d.H).First())
                .OrderBy(d => d.T)
                .ToList();

            double currentT = 0;
            double currentCx = 0.5; // Start center
            double currentCy = 0.5;

            double velocityX = 0;
            double velocityY = 0;

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

                // Calculate spring physics for X
                if (Math.Abs(targetCx - currentCx) > deadzone)
                {
                    double forceX = springConstant * (targetCx - currentCx) - damping * velocityX;
                    velocityX += forceX * dt;
                    currentCx += velocityX * dt;
                }
                else
                {
                    // Decay velocity if inside deadzone for super smooth stop
                    velocityX *= 0.9;
                    currentCx += velocityX * dt;
                }

                // Calculate spring physics for Y
                if (Math.Abs(targetCy - currentCy) > deadzone)
                {
                    double forceY = springConstant * (targetCy - currentCy) - damping * velocityY;
                    velocityY += forceY * dt;
                    currentCy += velocityY * dt;
                }
                else
                {
                    velocityY *= 0.9;
                    currentCy += velocityY * dt;
                }

                // Clamp to safe boundaries so 9:16 crop doesn't go out of bounds
                double cropW = 9.0 / 16.0;
                double minCx = cropW / 2.0;
                double maxCx = 1.0 - (cropW / 2.0);

                currentCx = Math.Clamp(currentCx, minCx, maxCx);

                // Keep Y locked to center for modern vertical video style unless dramatic change
                currentCy = Math.Clamp(currentCy, 0.5, 0.5);

                track.Add(new CropPoint
                {
                    T = currentT,
                    Cx = currentCx,
                    Cy = currentCy
                });

                currentT += dt;
            }

            return track;
        }
    }
}
