using System;
using System.Collections.Generic;
using System.Linq;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public static class ClipEditMath
    {
        public const double MinClipSeconds = 1.0;
        public const double MaxClipSeconds = 180.0;
        public const double MinRangeSeconds = 0.02;
        public const double MergeGapSeconds = 0.08;

        public static List<TimeRange> NormalizeRanges(IEnumerable<TimeRange> ranges, TimeSpan clipStart, TimeSpan clipEnd)
        {
            if (ranges == null) return new List<TimeRange>();

            var validRanges = new List<TimeRange>();

            foreach (var range in ranges)
            {
                var s = range.Start < clipStart ? clipStart : range.Start;
                var e = range.End > clipEnd ? clipEnd : range.End;

                if (s < e && (e - s).TotalSeconds >= MinRangeSeconds)
                {
                    validRanges.Add(new TimeRange(s, e));
                }
            }

            validRanges.Sort((a, b) => a.Start.CompareTo(b.Start));

            var merged = new List<TimeRange>();
            foreach (var r in validRanges)
            {
                if (merged.Count == 0)
                {
                    merged.Add(r);
                }
                else
                {
                    var last = merged[merged.Count - 1];
                    if ((r.Start - last.End).TotalSeconds <= MergeGapSeconds)
                    {
                        var maxEnd = r.End > last.End ? r.End : last.End;
                        merged[merged.Count - 1] = new TimeRange(last.Start, maxEnd);
                    }
                    else
                    {
                        merged.Add(r);
                    }
                }
            }

            return merged;
        }

        public static List<TimeRange> MergeRanges(IEnumerable<TimeRange> a, IEnumerable<TimeRange> b, TimeSpan clipStart, TimeSpan clipEnd)
        {
            var combined = new List<TimeRange>();
            if (a != null) combined.AddRange(a);
            if (b != null) combined.AddRange(b);

            return NormalizeRanges(combined, clipStart, clipEnd);
        }

        public static TimeSpan KeptDuration(TimeSpan clipStart, TimeSpan clipEnd, IEnumerable<TimeRange> normalizedRanges)
        {
            var total = clipEnd - clipStart;
            if (total < TimeSpan.Zero) return TimeSpan.Zero;

            if (normalizedRanges != null)
            {
                foreach (var r in normalizedRanges)
                {
                    total -= (r.End - r.Start);
                }
            }

            return total < TimeSpan.Zero ? TimeSpan.Zero : total;
        }

        public static bool TrySetTrim(ClipCandidate clip, TimeSpan newStart, TimeSpan newEnd)
        {
            if (newStart < TimeSpan.Zero) return false;
            if (newEnd <= newStart) return false;

            var dur = (newEnd - newStart).TotalSeconds;
            if (dur < MinClipSeconds || dur > MaxClipSeconds) return false;

            clip.StartTime = newStart;
            clip.EndTime = newEnd;
            clip.DeletedRanges = NormalizeRanges(clip.DeletedRanges, newStart, newEnd);

            return true;
        }
    }
}
