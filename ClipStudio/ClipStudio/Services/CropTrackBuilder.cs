using System;
using System.Collections.Generic;
using System.Linq;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public class CropTrackBuilder
    {
        private const double MinFaceHeight = 0.06;
        private const double GroupWindowSeconds = 1.0;
        private const double MatchDistance = 0.12;
        private const double SwitchAreaRatio = 1.3;
        private const double SwitchDwellSeconds = 2.0;
        private const double LostSeconds = 2.0;
        private const double MinHoldSeconds = 2.0;
        private const double ComfortHalfWidth = 0.03;
        private const double CameraTau = 0.6;
        private const double MaxPanSpeed = 0.15;
        private const double DetectionStaleSeconds = 0.5;

        public class CropPoint
        {
            public double T { get; set; }
            public double Cx { get; set; }
            public double Cy { get; set; }
        }

        private class Participant
        {
            public double Cx;
            public double Cy;
            public double W;
            public double H;
            public double Area => W * H;
        }

        private class DetectionGroup
        {
            public double T;
            public List<Participant> Participants = new List<Participant>();
        }

        public List<CropPoint> BuildTrack(List<FaceDetection> detections, double videoDuration, double fps, int sourceWidth, int sourceHeight)
        {
            var track = new List<CropPoint>();
            if (sourceWidth <= 0 || sourceHeight <= 0 || detections == null || detections.Count == 0)
            {
                // Return default center track
                track.Add(new CropPoint { T = 0, Cx = 0.5, Cy = 0.5 });
                track.Add(new CropPoint { T = videoDuration, Cx = 0.5, Cy = 0.5 });
                return track;
            }

            double cropW = (double)CropMath.TargetWidth(sourceHeight) / sourceWidth;
            if (cropW >= 1.0)
            {
                track.Add(new CropPoint { T = 0, Cx = 0.5, Cy = 0.5 });
                track.Add(new CropPoint { T = videoDuration, Cx = 0.5, Cy = 0.5 });
                return track;
            }

            double minCx = cropW / 2.0;
            double maxCx = 1.0 - (cropW / 2.0);

            // Filter and group detections
            var filteredDetections = detections.Where(d => d.H >= MinFaceHeight).ToList();
            var groupedDetections = filteredDetections
                .GroupBy(d => d.T)
                .Select(g =>
                {
                    double maxH = g.Max(d => d.H);
                    var participants = g.Where(d => d.H >= 0.6 * maxH).Select(d => new Participant { Cx = d.Cx, Cy = d.Cy, W = d.W, H = d.H }).ToList();
                    return new DetectionGroup { T = g.Key, Participants = participants };
                })
                .OrderBy(g => g.T)
                .ToList();

            double dt = 1.0 / fps;
            int totalSteps = (int)Math.Ceiling(videoDuration / dt) + 1;

            // Pass 1: compute targets per output step
            double[] stepTargets = new double[totalSteps];
            bool[] stepSwitches = new bool[totalSteps];

            bool hasInitialTarget = false;
            double initialTarget = 0.5;

            double target = 0.5;
            bool groupMode = false;
            Participant? tracked = null;
            double challengeTimer = 0.0;
            double lostTimer = 0.0;
            double timeSinceLastSwitch = double.MaxValue;

            for (int i = 0; i < totalSteps; i++)
            {
                double t = i * dt;

                // Find latest fresh sample
                var freshGroups = groupedDetections.Where(g => g.T <= t && (t - g.T) <= DetectionStaleSeconds).ToList();
                var latestGroup = freshGroups.LastOrDefault();

                bool switchedThisStep = false;

                if (latestGroup == null || latestGroup.Participants.Count == 0)
                {
                    // No detections
                    challengeTimer = 0.0;
                    if (tracked != null && !groupMode)
                    {
                        lostTimer += dt;
                    }
                    timeSinceLastSwitch += dt;
                }
                else
                {
                    var participants = latestGroup.Participants;

                    // Group mode condition
                    var recentGroups = groupedDetections.Where(g => g.T <= t && (t - g.T) <= GroupWindowSeconds).ToList();
                    double span = 0;
                    if (recentGroups.Count > 0)
                    {
                        var allRecentParticipants = recentGroups.SelectMany(g => g.Participants).ToList();
                        if (allRecentParticipants.Count > 0)
                        {
                            double left = allRecentParticipants.Min(p => p.Cx - p.W / 2);
                            double right = allRecentParticipants.Max(p => p.Cx + p.W / 2);
                            span = right - left;
                        }
                    }

                    if (!groupMode && participants.Count >= 2 && span <= 0.85 * cropW)
                    {
                        groupMode = true;
                        switchedThisStep = true;
                        timeSinceLastSwitch = 0;
                    }
                    else if (groupMode && span > 0.95 * cropW)
                    {
                        groupMode = false;
                        switchedThisStep = true;
                        // entering single-subject from group mode -> largest participant as tracked
                        tracked = participants.OrderByDescending(p => p.Area).First();
                        challengeTimer = 0;
                        lostTimer = 0;
                        timeSinceLastSwitch = 0;
                    }

                    if (groupMode)
                    {
                        var allRecentParticipants = recentGroups.SelectMany(g => g.Participants).ToList();
                        double left = allRecentParticipants.Min(p => p.Cx - p.W / 2);
                        double right = allRecentParticipants.Max(p => p.Cx + p.W / 2);
                        target = (left + right) / 2.0;
                        challengeTimer = 0;
                        lostTimer = 0;
                    }
                    else
                    {
                        // Single-subject mode
                        if (tracked == null)
                        {
                            tracked = participants.OrderByDescending(p => p.Area).First();
                            switchedThisStep = true;
                            challengeTimer = 0;
                            lostTimer = 0;
                            timeSinceLastSwitch = 0;
                        }

                        Participant? match = participants
                            .Where(p => Math.Abs(p.Cx - tracked.Cx) <= MatchDistance)
                            .OrderBy(p => Math.Abs(p.Cx - tracked.Cx))
                            .FirstOrDefault();

                        if (match != null)
                        {
                            tracked = match;
                            lostTimer = 0;
                        }
                        else
                        {
                            lostTimer += dt;
                        }

                        // Challenge logic
                        if (match != null)
                        {
                            Participant? challenger = participants.Where(p => p != match).OrderByDescending(p => p.Area).FirstOrDefault();
                            if (challenger != null && challenger.Area > SwitchAreaRatio * tracked.Area)
                            {
                                challengeTimer += dt;
                            }
                            else
                            {
                                challengeTimer = 0;
                            }
                        }
                        else
                        {
                            challengeTimer = 0;
                        }

                        timeSinceLastSwitch += dt;

                        // Switch conditions
                        if (challengeTimer >= SwitchDwellSeconds && timeSinceLastSwitch >= MinHoldSeconds)
                        {
                            Participant challenger = participants.Where(p => p != match).OrderByDescending(p => p.Area).First();
                            tracked = challenger;
                            switchedThisStep = true;
                            challengeTimer = 0;
                            lostTimer = 0;
                            timeSinceLastSwitch = 0;
                        }
                        else if (lostTimer >= LostSeconds)
                        {
                            tracked = participants.OrderByDescending(p => p.Area).First();
                            switchedThisStep = true;
                            challengeTimer = 0;
                            lostTimer = 0;
                            timeSinceLastSwitch = 0;
                        }

                        target = tracked.Cx;
                    }
                }

                target = Math.Clamp(target, minCx, maxCx);

                if (!hasInitialTarget && latestGroup != null && latestGroup.Participants.Count > 0)
                {
                    hasInitialTarget = true;
                    initialTarget = target;
                    switchedThisStep = true; // First acquisition
                }

                stepTargets[i] = target;
                stepSwitches[i] = switchedThisStep;
            }

            // Back-fill targets
            if (hasInitialTarget)
            {
                for (int i = 0; i < totalSteps; i++)
                {
                    if (stepSwitches[i]) break; // Reached the first acquisition
                    stepTargets[i] = initialTarget;
                }
            }
            else
            {
                for (int i = 0; i < totalSteps; i++) stepTargets[i] = 0.5;
            }

            // Pass 2: Camera filter
            double cam = stepTargets[0];
            for (int i = 0; i < totalSteps; i++)
            {
                double t = i * dt;
                target = stepTargets[i];

                double err = target - cam;
                double desired;
                if (Math.Abs(err) <= ComfortHalfWidth)
                {
                    desired = cam;
                }
                else
                {
                    desired = cam + Math.Sign(err) * (Math.Abs(err) - ComfortHalfWidth);
                }

                if (stepSwitches[i] && Math.Abs(target - cam) > 0.8 * cropW)
                {
                    cam = target; // Hard cut
                }
                else
                {
                    double step = (desired - cam) * (1 - Math.Exp(-dt / CameraTau));
                    double maxStep = MaxPanSpeed * dt;
                    step = Math.Clamp(step, -maxStep, maxStep);
                    cam += step;
                }

                cam = Math.Clamp(cam, minCx, maxCx);

                track.Add(new CropPoint { T = t, Cx = cam, Cy = 0.5 });
            }

            return track;
        }
    }
}
