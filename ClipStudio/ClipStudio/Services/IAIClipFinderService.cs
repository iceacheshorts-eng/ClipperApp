using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public interface IAIClipFinderService
    {
        Task<List<ClipCandidate>> GetHighlightsAsync(
            IReadOnlyList<TranscriptSegment> transcript,
            int count,
            double minSeconds,
            double maxSeconds,
            CancellationToken ct);
    }
}
