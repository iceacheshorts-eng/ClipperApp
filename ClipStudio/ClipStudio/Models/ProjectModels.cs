using System;

namespace ClipStudio.Models
{
    public sealed class TranscriptCacheFile
    {
        public int SchemaVersion { get; set; }
        public string SourceKey { get; set; } = string.Empty;
        public string ModelFileName { get; set; } = string.Empty;
        public long ModelFileSize { get; set; }
        public bool FillerPrompt { get; set; }
        public bool WordLevel { get; set; }
        public DateTime SavedUtc { get; set; }
        public TranscriptionResult Transcription { get; set; } = new();
    }
}
