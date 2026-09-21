using System;
using System.Collections.Generic;

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

    public sealed class SavedClip
    {
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public double Score { get; set; }
        public string Reason { get; set; } = "";
        public string Transcript { get; set; } = "";
    }

    public sealed class SavedClipEdit
    {
        public TimeSpan OriginalStart { get; set; }
        public TimeSpan OriginalEnd { get; set; }
        public TimeSpan Start { get; set; }
        public TimeSpan End { get; set; }
        public List<TimeRange> DeletedRanges { get; set; } = new();
        public List<WordTiming>? EditorWords { get; set; }
        public string Transcript { get; set; } = string.Empty;
        public DateTime SavedUtc { get; set; }
    }

    public sealed class ClipEditsFile
    {
        public int SchemaVersion { get; set; }
        public string SourceKey { get; set; } = string.Empty;
        public List<SavedClipEdit> Edits { get; set; } = new();
    }

    public sealed class HighlightsCacheFile
    {
        public int SchemaVersion { get; set; }
        public string SourceKey { get; set; } = string.Empty;
        public string ConfigKey { get; set; } = string.Empty;
        public int PerChunkCount { get; set; }
        public string ModelId { get; set; } = string.Empty;
        public DateTime SavedUtc { get; set; }
        public System.Collections.Generic.List<SavedClip> Clips { get; set; } = new();
    }
}
