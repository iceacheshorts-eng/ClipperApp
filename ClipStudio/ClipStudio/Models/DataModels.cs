using System;

namespace ClipStudio.Models
{
    public class ClipCandidate
    {
        public TimeSpan StartTime { get; set; }
        public TimeSpan EndTime { get; set; }
        public double Duration => (EndTime - StartTime).TotalSeconds;
        public double Score { get; set; }
        public string Transcript { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public bool IsApproved { get; set; } = true;
    }

    public class FaceDetection
    {
        public double T { get; set; }
        public string Type { get; set; } = "face";
        public double Cx { get; set; }
        public double Cy { get; set; }
        public double W { get; set; }
        public double H { get; set; }
    }

    public enum ContentStyle
    {
        Balanced,
        HighEnergy,
        Podcast,
        Storytelling,
        ViewerDriven
    }
}
