using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ClipStudio.Models;

namespace ClipStudio.Services
{
    public class AIClipFinderService : IAIClipFinderService
    {
        private readonly IActivityLogger _logger;
        private readonly HttpClient _httpClient;

        public AIClipFinderService(IActivityLogger logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
        }

        public async Task<List<ClipCandidate>> GetHighlightsAsync(
            IReadOnlyList<TranscriptSegment> transcript,
            int count,
            double minSeconds,
            double maxSeconds,
            CancellationToken ct)
        {
            string apiKey = Environment.GetEnvironmentVariable(GroqConfig.EnvVarName) ?? string.Empty;
            if (string.IsNullOrEmpty(apiKey))
            {
                _logger.Log($"Environment variable {GroqConfig.EnvVarName} not set. AI extraction will be skipped.");
                return new List<ClipCandidate>();
            }

            if (transcript.Count == 0)
            {
                return new List<ClipCandidate>();
            }

            // Chunk transcripts if they are extremely long to avoid LLM context / output issues
            var chunks = ChunkTranscript(transcript, maxSentences: 150);
            var allCandidates = new List<ClipCandidate>();

            // Request highlights per chunk (sequentially to respect basic rate limits)
            foreach (var chunk in chunks)
            {
                var candidates = await GetHighlightsForChunkAsync(chunk, apiKey, count, minSeconds, maxSeconds, transcript, ct);
                allCandidates.AddRange(candidates);
            }

            // Return top unique results
            var finalCandidates = new List<ClipCandidate>();
            foreach (var c in allCandidates.OrderByDescending(x => x.Score))
            {
                if (finalCandidates.Count >= count) break;
                
                // Ensure no overlapping clips
                bool overlaps = finalCandidates.Any(existing => 
                    !(c.StartTime.TotalSeconds >= existing.EndTime.TotalSeconds || c.EndTime.TotalSeconds <= existing.StartTime.TotalSeconds));
                
                if (!overlaps)
                {
                    finalCandidates.Add(c);
                }
            }

            return finalCandidates.OrderBy(c => c.StartTime).ToList();
        }

        private async Task<List<ClipCandidate>> GetHighlightsForChunkAsync(
            List<TranscriptSegment> chunk,
            string apiKey,
            int count,
            double minSeconds,
            double maxSeconds,
            IReadOnlyList<TranscriptSegment> fullTranscript,
            CancellationToken ct)
        {
            var promptText = new StringBuilder();
            promptText.AppendLine($"You are an AI video editor. Find up to {count} best highlights from the transcript below.");
            promptText.AppendLine($"Choose self-contained moments that start and end on complete thoughts.");
            promptText.AppendLine($"The ideal length for each clip is between {minSeconds} and {maxSeconds} seconds, but focus on the complete thought.");
            promptText.AppendLine("Format the output as a JSON array of objects, with each object having properties: StartSegment (int), EndSegment (int), Score (float 0.0-1.0), and Reason (short string).");
            promptText.AppendLine("Here is the numbered transcript:\n");

            foreach (var seg in chunk)
            {
                promptText.AppendLine($"[{seg.Index}] {seg.Text}");
            }

            var payload = new
            {
                model = GroqConfig.ModelId,
                messages = new[]
                {
                    new { role = "user", content = promptText.ToString() }
                },
                response_format = new { type = "json_object" }
            };

            var requestJson = JsonSerializer.Serialize(payload);
            using var content = new StringContent(requestJson, Encoding.UTF8, "application/json");

            HttpResponseMessage? response = null;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, GroqConfig.BaseUrl);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                request.Content = content;
                
                response = await _httpClient.SendAsync(request, ct);
                
                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                {
                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                    _logger.Log($"Rate limited by Groq API. Retrying after {retryAfter.TotalSeconds} seconds...");
                    await Task.Delay(retryAfter, ct);

                    // Recreate request because it was disposed
                    using var retryRequest = new HttpRequestMessage(HttpMethod.Post, GroqConfig.BaseUrl);
                    retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    retryRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    
                    response = await _httpClient.SendAsync(retryRequest, ct);
                }

                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<GroqResponse>(responseString);
                var contentString = result?.Choices?.FirstOrDefault()?.Message?.Content;

                if (string.IsNullOrEmpty(contentString))
                {
                    throw new Exception("Empty response from Groq API.");
                }
                
                List<HighlightResponse> highlights;
                try
                {
                    var parsed = JsonSerializer.Deserialize<GroqHighlightsResponse>(contentString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    highlights = parsed?.Highlights ?? new List<HighlightResponse>();
                    if (highlights.Count == 0)
                    {
                        highlights = JsonSerializer.Deserialize<List<HighlightResponse>>(contentString, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<HighlightResponse>();
                    }
                }
                catch
                {
                     _logger.Log($"Malformed JSON from LLM. Retrying once...");
                    using var retryRequest = new HttpRequestMessage(HttpMethod.Post, GroqConfig.BaseUrl);
                    retryRequest.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    retryRequest.Content = new StringContent(requestJson, Encoding.UTF8, "application/json");
                    
                    var response2 = await _httpClient.SendAsync(retryRequest, ct);
                    response2.EnsureSuccessStatusCode();
                    var responseString2 = await response2.Content.ReadAsStringAsync(ct);
                    var result2 = JsonSerializer.Deserialize<GroqResponse>(responseString2);
                    var contentString2 = result2?.Choices?.FirstOrDefault()?.Message?.Content;
                    
                    if (string.IsNullOrEmpty(contentString2)) throw new Exception("Empty response on retry.");
                    
                    var parsed = JsonSerializer.Deserialize<GroqHighlightsResponse>(contentString2, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                    highlights = parsed?.Highlights ?? new List<HighlightResponse>();
                    if (highlights.Count == 0)
                    {
                        highlights = JsonSerializer.Deserialize<List<HighlightResponse>>(contentString2, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new List<HighlightResponse>();
                    }
                }

                var candidates = new List<ClipCandidate>();
                foreach (var h in highlights)
                {
                    if (h.StartSegment < 0 || h.EndSegment >= fullTranscript.Count || h.StartSegment > h.EndSegment)
                        continue; // Invalid range

                    var startSeg = fullTranscript[h.StartSegment];
                    var endSeg = fullTranscript[h.EndSegment];

                    // Snap to sentence boundaries
                    var actualStart = SnapToSentenceStart(fullTranscript, h.StartSegment, TimeSpan.FromSeconds(5));
                    var actualEnd = SnapToSentenceEnd(fullTranscript, h.EndSegment, TimeSpan.FromSeconds(5));

                    var duration = (actualEnd - actualStart).TotalSeconds;

                    // Clamp duration
                    if (duration < minSeconds * 0.6)
                        continue; // Too short
                    if (duration > maxSeconds * 1.3)
                    {
                        // Too long, clamp it
                        actualEnd = actualStart + TimeSpan.FromSeconds(maxSeconds * 1.3);
                    }

                    // Build transcript text
                    var textSegments = fullTranscript.Where(s => s.Start >= actualStart && s.End <= actualEnd).Select(s => s.Text);
                    string text = string.Join(" ", textSegments).Trim();

                    candidates.Add(new ClipCandidate
                    {
                        StartTime = actualStart,
                        EndTime = actualEnd,
                        Score = h.Score,
                        Reason = h.Reason ?? string.Empty,
                        Transcript = text
                    });
                }

                return candidates;
            }
            catch (Exception ex)
            {
                _logger.Log($"LLM AI Clip finding chunk failed: {ex.Message}. Skipping chunk.");
                return new List<ClipCandidate>();
            }
        }
        
        private List<List<TranscriptSegment>> ChunkTranscript(IReadOnlyList<TranscriptSegment> transcript, int maxSentences)
        {
            var chunks = new List<List<TranscriptSegment>>();
            var currentChunk = new List<TranscriptSegment>();

            foreach (var seg in transcript)
            {
                currentChunk.Add(seg);
                if (currentChunk.Count >= maxSentences)
                {
                    chunks.Add(currentChunk);
                    currentChunk = new List<TranscriptSegment>();
                }
            }

            if (currentChunk.Count > 0)
            {
                chunks.Add(currentChunk);
            }

            return chunks;
        }

        private TimeSpan SnapToSentenceStart(IReadOnlyList<TranscriptSegment> transcript, int index, TimeSpan window)
        {
            var targetTime = transcript[index].Start;
            // Go backwards looking for a segment that follows sentence-ending punctuation
            for (int i = index - 1; i >= 0; i--)
            {
                if (targetTime - transcript[i].End > window)
                    break;

                var text = transcript[i].Text.TrimEnd();
                if (text.EndsWith(".") || text.EndsWith("!") || text.EndsWith("?"))
                {
                    return transcript[i + 1].Start; // The start of the sentence after this punctuation
                }
            }
            return transcript[index].Start;
        }

        private TimeSpan SnapToSentenceEnd(IReadOnlyList<TranscriptSegment> transcript, int index, TimeSpan window)
        {
            var targetTime = transcript[index].End;
            for (int i = index; i < transcript.Count; i++)
            {
                if (transcript[i].Start - targetTime > window)
                    break;
                
                var text = transcript[i].Text.TrimEnd();
                if (text.EndsWith(".") || text.EndsWith("!") || text.EndsWith("?"))
                {
                    return transcript[i].End;
                }
            }
            return transcript[index].End;
        }

        private class GroqResponse
        {
            [JsonPropertyName("choices")]
            public List<Choice>? Choices { get; set; }
        }

        private class Choice
        {
            [JsonPropertyName("message")]
            public Message? Message { get; set; }
        }

        private class Message
        {
            [JsonPropertyName("content")]
            public string? Content { get; set; }
        }

        private class GroqHighlightsResponse
        {
            public List<HighlightResponse>? Highlights { get; set; }
        }

        private class HighlightResponse
        {
            public int StartSegment { get; set; }
            public int EndSegment { get; set; }
            public float Score { get; set; }
            public string? Reason { get; set; }
        }
    }
}
