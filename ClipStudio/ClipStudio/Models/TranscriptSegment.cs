using System;

namespace ClipStudio.Models
{
    public sealed record TranscriptSegment(int Index, TimeSpan Start, TimeSpan End, string Text);
}
