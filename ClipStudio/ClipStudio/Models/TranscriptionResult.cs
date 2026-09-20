using System;
using System.Collections.Generic;

namespace ClipStudio.Models
{
    public sealed record WordTiming(string Text, TimeSpan Start, TimeSpan End, bool Estimated);

    public sealed class TranscriptionResult
    {
        public List<TranscriptSegment> Segments { get; init; } = new();
        public List<WordTiming> Words { get; init; } = new();
    }
}
