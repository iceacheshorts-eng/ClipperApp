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
        private const double StackedMinSeparation = 0.40;
        private const double LayoutDwellSeconds = 1.5;
        private const double MinLayoutHoldSeconds = 4.0;

        public class CropPoint
        {
            public double T { get; set; }
            public double Cx { get; set; }
            public double Cy { get; set; }
            public CropLayout Layout { get; set; } = CropLayout.Single;
            public double Cx2 { get; set; } = 0.5;
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
            CropLayout[] stepLayouts = new CropLayout[totalSteps];
            double[] stepTargets2 = new double[totalSteps];
            bool[] stepLayoutSwitched = new bool[totalSteps];

            bool hasInitialTarget = false;
            double initialTarget = 0.5;
            double initialTarget2 = 0.5;
            CropLayout initialLayout = CropLayout.Single;

            double target = 0.5;
            double target2 = 0.5;
            bool groupMode = false;
            Participant? tracked = null;
            double challengeTimer = 0.0;
            double lostTimer = 0.0;
            double timeSinceLastSwitch = double.MaxValue;

            CropLayout? activeLayout = null;
            CropLayout pendingLayout = CropLayout.Single;
            double pendingTimer = 0.0;
            double timeSinceLayoutChange = double.MaxValue;

            int hi = -1;
            int lo = 0;

            for (int i = 0; i < totalSteps; i++)
            {
                double t = i * dt;

                while (hi + 1 < groupedDetections.Count && groupedDetections[hi + 1].T <= t) hi++;
                while (lo < groupedDetections.Count && t - groupedDetections[lo].T > GroupWindowSeconds) lo++;

                var latestGroup = (hi >= 0 && t - groupedDetections[hi].T <= DetectionStaleSeconds) ? groupedDetections[hi] : null;

                bool switchedThisStep = false;
                bool layoutSwitchedThisStep = false;

                if (latestGroup == null || latestGroup.Participants.Count == 0)
                {
                    // No detections
                    challengeTimer = 0.0;
                    if (activeLayout == CropLayout.Single)
                    {
                        if (tracked != null && !groupMode)
                        {
                            lostTimer += dt;
                        }
                        timeSinceLastSwitch += dt;
                    }

                    pendingTimer = 0.0;
                    timeSinceLayoutChange += dt;
                }
                else
                {
                    var participants = latestGroup.Participants;
                    int n = participants.Count;

                    double span = 0;
                    double left = double.MaxValue;
                    double right = double.MinValue;

                    if (lo <= hi)
                    {
                        for (int gi = lo; gi <= hi; gi++)
                        {
                            foreach (var p in groupedDetections[gi].Participants)
                            {
                                if (p.Cx - p.W / 2 < left) left = p.Cx - p.W / 2;
                                if (p.Cx + p.W / 2 > right) right = p.Cx + p.W / 2;
                            }
                        }
                        if (left <= right)
                        {
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

                    CropLayout candidateLayout;
                    if (groupMode || n == 1)
                    {
                        candidateLayout = CropLayout.Single;
                    }
                    else if (n == 2 && CropMath.CanStack(sourceWidth, sourceHeight))
                    {
                        var sorted = participants.OrderBy(p => p.Cx).ToList();
                        if (Math.Abs(sorted[1].Cx - sorted[0].Cx) >= StackedMinSeparation)
                            candidateLayout = CropLayout.Stacked;
                        else
                            candidateLayout = CropLayout.BlurFit;
                    }
                    else
                    {
                        candidateLayout = CropLayout.BlurFit;
                    }

                    if (activeLayout == null)
                    {
                        activeLayout = candidateLayout;
                    }
                    else
                    {
                        if (candidateLayout == activeLayout)
                        {
                            pendingTimer = 0;
                        }
                        else if (candidateLayout == pendingLayout)
                        {
                            pendingTimer += dt;
                        }
                        else
                        {
                            pendingLayout = candidateLayout;
                            pendingTimer = dt;
                        }

                        if (pendingTimer >= LayoutDwellSeconds && timeSinceLayoutChange >= MinLayoutHoldSeconds)
                        {
                            activeLayout = candidateLayout;
                            layoutSwitchedThisStep = true;
                            pendingTimer = 0;
                            timeSinceLayoutChange = 0;
                        }
                    }

                    timeSinceLayoutChange += dt;

                    if (activeLayout == CropLayout.Single)
                    {
                        if (layoutSwitchedThisStep)
                        {
                            tracked = participants.OrderByDescending(p => p.Area).First();
                            challengeTimer = 0;
                            lostTimer = 0;
                            timeSinceLastSwitch = 0;
                        }

                        if (groupMode)
                        {
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
                    else if (activeLayout == CropLayout.Stacked)
                    {
                        if (participants.Count >= 2)
                        {
                            var sorted = participants.OrderBy(p => p.Cx).ToList();
                            double regionFrac = CropMath.StackedRegionWidth(sourceHeight) / (double)sourceWidth;
                            target = Math.Clamp(sorted[0].Cx, regionFrac / 2.0, 1.0 - regionFrac / 2.0);
                            target2 = Math.Clamp(sorted[sorted.Count - 1].Cx, regionFrac / 2.0, 1.0 - regionFrac / 2.0);
                        }
                    }
                    else if (activeLayout == CropLayout.BlurFit)
                    {
                        target = 0.5;
                        target2 = 0.5;
                    }
                }

                if (activeLayout != CropLayout.Stacked)
                {
                    target = Math.Clamp(target, minCx, maxCx);
                }

                if (!hasInitialTarget && latestGroup != null && latestGroup.Participants.Count > 0)
                {
                    hasInitialTarget = true;
                    initialTarget = target;
                    initialTarget2 = target2;
                    initialLayout = activeLayout ?? CropLayout.Single;
                    switchedThisStep = true; // First acquisition
                }

                stepTargets[i] = target;
                stepTargets2[i] = target2;
                stepSwitches[i] = switchedThisStep;
                stepLayouts[i] = activeLayout ?? CropLayout.Single;
                stepLayoutSwitched[i] = layoutSwitchedThisStep;
            }

            // Back-fill targets
            if (hasInitialTarget)
            {
                for (int i = 0; i < totalSteps; i++)
                {
                    if (stepSwitches[i]) break; // Reached the first acquisition
                    stepTargets[i] = initialTarget;
                    stepTargets2[i] = initialTarget2;
                    stepLayouts[i] = initialLayout;
                }
            }
            else
            {
                for (int i = 0; i < totalSteps; i++)
                {
                    stepTargets[i] = 0.5;
                    stepTargets2[i] = 0.5;
                    stepLayouts[i] = CropLayout.Single;
                }
            }

            // Pass 2: Camera filter
            double cam = stepTargets[0];
            double cam2 = stepTargets2[0];
            for (int i = 0; i < totalSteps; i++)
            {
                double t = i * dt;
                target = stepTargets[i];
                target2 = stepTargets2[i];

                if (stepLayoutSwitched[i])
                {
                    cam = target;
                    cam2 = target2;
                }
                else
                {
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

                    double err2 = target2 - cam2;
                    double desired2;
                    if (Math.Abs(err2) <= ComfortHalfWidth)
                    {
                        desired2 = cam2;
                    }
                    else
                    {
                        desired2 = cam2 + Math.Sign(err2) * (Math.Abs(err2) - ComfortHalfWidth);
                    }
                    double step2 = (desired2 - cam2) * (1 - Math.Exp(-dt / CameraTau));
                    double maxStep2 = MaxPanSpeed * dt;
                    step2 = Math.Clamp(step2, -maxStep2, maxStep2);
                    cam2 += step2;
                }

                if (stepLayouts[i] == CropLayout.Stacked)
                {
                    double regionFrac = CropMath.StackedRegionWidth(sourceHeight) / (double)sourceWidth;
                    cam = Math.Clamp(cam, regionFrac / 2.0, 1.0 - regionFrac / 2.0);
                    cam2 = Math.Clamp(cam2, regionFrac / 2.0, 1.0 - regionFrac / 2.0);
                }
                else
                {
                    cam = Math.Clamp(cam, minCx, maxCx);
                    cam2 = Math.Clamp(cam2, minCx, maxCx);
                }

                track.Add(new CropPoint { T = t, Cx = cam, Cy = 0.5, Layout = stepLayouts[i], Cx2 = cam2 });
            }

            return track;
        }
    }
}
