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

        public string? CacheSourceKey { get; set; }
        public bool ReuseSavedPicks { get; set; } = true;

        private const int PromptVersion = 1; // bump whenever the prompt, schema or validation logic changes
        private const int ChunkSize = 80;
        private int _failedChunks;

        public AIClipFinderService(IActivityLogger logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
        }

        private const double AutoMinSeconds = 15.0;
        private const double AutoMinAcceptSeconds = 12.0;
        private const double AutoMaxSeconds = 60.0;

        public async Task<List<ClipCandidate>> GetHighlightsAsync(
            IReadOnlyList<TranscriptSegment> transcript,
            int count,
            double minSeconds,
            double maxSeconds,
            bool autoLength,
            CancellationToken ct)
        {
            _failedChunks = 0;
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
            var chunks = ChunkTranscript(transcript, maxSentences: ChunkSize);
            var allCandidates = new List<ClipCandidate>();

            string? configKey = null;
            if (CacheSourceKey != null)
            {
                double formatMin = autoLength ? 0 : minSeconds;
                double formatMax = autoLength ? 0 : maxSeconds;
                configKey = $"v{PromptVersion}|c{ChunkSize}|m{GroqConfig.ModelId}|a{autoLength}|min{formatMin.ToString(System.Globalization.CultureInfo.InvariantCulture)}|max{formatMax.ToString(System.Globalization.CultureInfo.InvariantCulture)}|t{ProjectStore.TranscriptFingerprint(transcript)}";
            }

            bool loadedFromCache = false;
            if (ReuseSavedPicks && CacheSourceKey != null && configKey != null)
            {
                var cached = ProjectStore.TryLoadHighlights(CacheSourceKey, configKey);
                if (cached != null && cached.PerChunkCount >= count && cached.Clips.Count > 0)
                {
                    allCandidates = cached.Clips.Select(c => new ClipCandidate
                    {
                        StartTime = c.Start,
                        EndTime = c.End,
                        Score = c.Score,
                        Reason = c.Reason,
                        Transcript = c.Transcript
                    }).ToList();
                    _logger.Log($"Loaded {allCandidates.Count} saved AI picks (saved {cached.SavedUtc:yyyy-MM-dd HH:mm} UTC); skipping Groq");
                    loadedFromCache = true;
                }
            }

            if (!loadedFromCache)
            {
                int emptyChunks = 0;
                int chunkIndex = 1;
                // Request highlights per chunk (sequentially to respect basic rate limits)
                foreach (var chunk in chunks)
                {
                    _logger.Log($"Analyzing chunk {chunkIndex}/{chunks.Count}...");
                    var candidates = await GetHighlightsForChunkAsync(chunk, apiKey, count, minSeconds, maxSeconds, autoLength, transcript, ct);
                    if (candidates.Count == 0)
                    {
                        emptyChunks++;
                    }
                    allCandidates.AddRange(candidates);
                    chunkIndex++;
                }

                if (emptyChunks > 0)
                {
                    _logger.Log($"Groq: {emptyChunks} of {chunks.Count} chunks returned no clips");
                }

                if (CacheSourceKey != null && configKey != null && _failedChunks == 0 && allCandidates.Count > 0)
                {
                    ProjectStore.SaveHighlights(CacheSourceKey, configKey, count, GroqConfig.ModelId, allCandidates);
                    _logger.Log("Saved AI picks for reuse");
                }
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
            bool autoLength,
            IReadOnlyList<TranscriptSegment> fullTranscript,
            CancellationToken ct)
        {
            if (autoLength)
            {
                minSeconds = AutoMinSeconds;
                maxSeconds = AutoMaxSeconds;
            }

            var promptText = new StringBuilder();
            if (autoLength)
            {
                promptText.AppendLine($"You are a short-form video editor who picks the clips most likely to go viral on TikTok, Reels and Shorts. Find up to {count} clips.");
                promptText.AppendLine($"Each transcript line shows its start and end time in seconds. Choose each clip's length yourself: clip length = end time of endSegment minus start time of startSegment, and it must be between 15 and 60 seconds. Use only as long as the moment needs: a punchy moment can be 15-25 seconds, a story with a payoff can use up to 60. Never pad a clip to reach 60. Lengths should differ naturally between clips.");
                promptText.AppendLine($"Virality criteria: a strong hook in the first 3 seconds (bold claim, question, surprise, strong opinion); an emotional or curiosity payoff (humor, conflict, revelation, useful tip); fully self-contained with no missing context; a clear setup-to-payoff arc; ends right after the payoff on a finished sentence. Avoid intros, outros, filler, housekeeping and setup with no payoff.");
                promptText.AppendLine("Return a JSON object with a \"highlights\" array. Each item has: startSegment (int), endSegment (int), score (number 0.0-1.0), reason (short string).");
                promptText.AppendLine("score = likelihood of going viral, 0.0 to 1.0. Use the full range; 0.9 and above should be rare.");
                promptText.AppendLine("reason = one short sentence naming the hook and the payoff.");
                promptText.AppendLine("Use the numbers in square brackets exactly as shown. Do not renumber.");
                promptText.AppendLine("Here is the numbered transcript:\n");

                foreach (var seg in chunk)
                {
                    promptText.AppendLine($"[{seg.Index}] ({(int)Math.Round(seg.Start.TotalSeconds)}-{(int)Math.Round(seg.End.TotalSeconds)}s) {seg.Text}");
                }
            }
            else
            {
                promptText.AppendLine($"You are an AI video editor. Find up to {count} best highlights from the transcript below.");
                promptText.AppendLine($"Choose self-contained moments that start and end on complete thoughts.");
                promptText.AppendLine($"The ideal length for each clip is between {minSeconds} and {maxSeconds} seconds, but focus on the complete thought.");
                promptText.AppendLine("Return a JSON object with a \"highlights\" array. Each item has: startSegment (int), endSegment (int), score (number 0.0-1.0), reason (short string).");
                promptText.AppendLine("Use the numbers in square brackets exactly as shown. Do not renumber.");
                promptText.AppendLine("Here is the numbered transcript:\n");

                foreach (var seg in chunk)
                {
                    promptText.AppendLine($"[{seg.Index}] {seg.Text}");
                }
            }

            var minIndex = chunk.Count > 0 ? chunk[0].Index : 0;
            var maxIndex = chunk.Count > 0 ? chunk[^1].Index : 0;

            try
            {
                string requestJson = BuildPayload(promptText.ToString(), strictSchema: true);

                async Task<HttpResponseMessage> SendRequestAsync(string reqJson)
                {
                    var request = new HttpRequestMessage(HttpMethod.Post, GroqConfig.BaseUrl);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
                    request.Content = new StringContent(reqJson, Encoding.UTF8, "application/json");
                    return await _httpClient.SendAsync(request, ct);
                }

                HttpResponseMessage response = await SendRequestAsync(requestJson);

                if (!response.IsSuccessStatusCode)
                {
                    var responseBody = await response.Content.ReadAsStringAsync(ct);
                    var truncatedBody = responseBody.Length > 300 ? responseBody.Substring(0, 300) : responseBody;
                    _logger.Log($"Groq API error: {(int)response.StatusCode} - {truncatedBody}");

                    if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                    {
                        var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                        var wait = TimeSpan.FromSeconds(Math.Min(retryAfter.TotalSeconds, 60));
                        _logger.Log($"Rate limited by Groq API. Retrying after {wait.TotalSeconds} seconds...");
                        await Task.Delay(wait, ct);

                        response = await SendRequestAsync(requestJson);
                        if (!response.IsSuccessStatusCode)
                        {
                            _logger.Log("Rate limit persisted; skipping chunk");
                            _failedChunks++;
                            return new List<ClipCandidate>();
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                    {
                        var code = TryGetGroqErrorCode(responseBody);
                        bool isJsonValidateFailed = code == "json_validate_failed" || responseBody.Contains("json_validate_failed", StringComparison.OrdinalIgnoreCase);

                        if (isJsonValidateFailed)
                        {
                            _logger.Log("Groq returned json_validate_failed; retrying chunk without strict schema");
                            string retryJson = BuildPayload(promptText.ToString(), strictSchema: false);
                            response = await SendRequestAsync(retryJson);

                            if (!response.IsSuccessStatusCode)
                            {
                                var retryBody = await response.Content.ReadAsStringAsync(ct);
                                var retryTruncatedBody = retryBody.Length > 300 ? retryBody.Substring(0, 300) : retryBody;
                                _logger.Log($"Groq API retry error: {(int)response.StatusCode} - {retryTruncatedBody}");

                                if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests)
                                {
                                    var retryAfter = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5);
                                    var wait = TimeSpan.FromSeconds(Math.Min(retryAfter.TotalSeconds, 60));
                                    _logger.Log($"Rate limited by Groq API on retry. Retrying after {wait.TotalSeconds} seconds...");
                                    await Task.Delay(wait, ct);

                                    response = await SendRequestAsync(retryJson);
                                    if (!response.IsSuccessStatusCode)
                                    {
                                        _logger.Log("Rate limit persisted on retry; skipping chunk");
                                        _failedChunks++;
                                        return new List<ClipCandidate>();
                                    }
                                }
                                else if (response.StatusCode == System.Net.HttpStatusCode.BadRequest)
                                {
                                    var retryCode = TryGetGroqErrorCode(retryBody);
                                    bool retryIsJsonValidateFailed = retryCode == "json_validate_failed" || retryBody.Contains("json_validate_failed", StringComparison.OrdinalIgnoreCase);
                                    if (retryIsJsonValidateFailed)
                                    {
                                        _logger.Log("Groq returned json_validate_failed on retry; skipping chunk");
                                        _failedChunks++;
                                        return new List<ClipCandidate>();
                                    }
                                    throw new GroqApiException($"Configuration error on retry: {(int)response.StatusCode} - {retryTruncatedBody}");
                                }
                                else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                                         response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                                         response.StatusCode == System.Net.HttpStatusCode.NotFound)
                                {
                                    throw new GroqApiException($"Configuration error on retry: {(int)response.StatusCode} - {retryTruncatedBody}");
                                }
                                else if ((int)response.StatusCode >= 500)
                                {
                                    _logger.Log($"Server error {(int)response.StatusCode} from Groq API on retry. Skipping chunk.");
                                    _failedChunks++;
                                    return new List<ClipCandidate>();
                                }
                                else
                                {
                                    _failedChunks++;
                                    return new List<ClipCandidate>();
                                }
                            }
                        }
                        else
                        {
                            throw new GroqApiException($"Configuration error: {(int)response.StatusCode} - {truncatedBody}");
                        }
                    }
                    else if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized ||
                             response.StatusCode == System.Net.HttpStatusCode.Forbidden ||
                             response.StatusCode == System.Net.HttpStatusCode.NotFound)
                    {
                        throw new GroqApiException($"Configuration error: {(int)response.StatusCode} - {truncatedBody}");
                    }
                    else if ((int)response.StatusCode >= 500)
                    {
                        _logger.Log($"Server error {(int)response.StatusCode} from Groq API. Skipping chunk.");
                        _failedChunks++;
                        return new List<ClipCandidate>();
                    }
                    else
                    {
                        // Some other 4xx error on the first try that is not bad request, 401,403,404, or 429
                        throw new GroqApiException($"Configuration error: {(int)response.StatusCode} - {truncatedBody}");
                    }
                }

                response.EnsureSuccessStatusCode();

                var responseString = await response.Content.ReadAsStringAsync(ct);
                var result = JsonSerializer.Deserialize<GroqResponse>(responseString);
                var contentString = result?.Choices?.FirstOrDefault()?.Message?.Content;

                if (string.IsNullOrEmpty(contentString))
                {
                    _logger.Log("Empty response from Groq API. Skipping chunk.");
                    _failedChunks++;
                    return new List<ClipCandidate>();
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
                catch (JsonException)
                {
                    _logger.Log("Malformed JSON from LLM; skipping chunk.");
                    _failedChunks++;
                    return new List<ClipCandidate>();
                }

                var candidates = new List<ClipCandidate>();
                foreach (var h in highlights)
                {
                    if (!(minIndex <= h.StartSegment && h.StartSegment <= h.EndSegment && h.EndSegment <= maxIndex))
                    {
                        _logger.Log($"Rejecting highlight: indices out of chunk bounds ({h.StartSegment}-{h.EndSegment} not in {minIndex}-{maxIndex})");
                        continue; // Invalid range
                    }

                    if (h.StartSegment < 0 || h.EndSegment >= fullTranscript.Count)
                        continue; // Extra safety

                    var startSeg = fullTranscript[h.StartSegment];
                    var endSeg = fullTranscript[h.EndSegment];

                    // Snap to sentence boundaries
                    var actualStart = SnapToSentenceStart(fullTranscript, h.StartSegment, TimeSpan.FromSeconds(5));
                    var actualEnd = SnapToSentenceEnd(fullTranscript, h.EndSegment, TimeSpan.FromSeconds(5));

                    var duration = (actualEnd - actualStart).TotalSeconds;

                    // Check if too long
                    double maxLimitSeconds = autoLength ? AutoMaxSeconds : (maxSeconds * 1.3);
                    if (duration > maxLimitSeconds)
                    {
                        var maxEndTime = actualStart + TimeSpan.FromSeconds(maxLimitSeconds);
                        TimeSpan? newActualEnd = null;

                        // Walk backwards from h.EndSegment to h.StartSegment
                        for (int i = h.EndSegment; i >= h.StartSegment; i--)
                        {
                            var seg = fullTranscript[i];
                            if (seg.End <= maxEndTime)
                            {
                                var segText = seg.Text.TrimEnd();
                                if (segText.EndsWith(".") || segText.EndsWith("!") || segText.EndsWith("?"))
                                {
                                    newActualEnd = seg.End;
                                    break;
                                }
                            }
                        }

                        // If no punctuation found, just take the last segment within the limit
                        if (newActualEnd == null)
                        {
                            for (int i = h.EndSegment; i >= h.StartSegment; i--)
                            {
                                var seg = fullTranscript[i];
                                if (seg.End <= maxEndTime)
                                {
                                    newActualEnd = seg.End;
                                    break;
                                }
                            }
                        }

                        if (newActualEnd == null)
                        {
                            _logger.Log("Rejecting highlight: no segment end found within max duration limit.");
                            continue;
                        }

                        actualEnd = newActualEnd.Value;
                        duration = (actualEnd - actualStart).TotalSeconds;
                    }

                    // Short-clip rescue
                    if (autoLength && duration < AutoMinAcceptSeconds)
                    {
                        for (int i = h.EndSegment + 1; i <= maxIndex; i++)
                        {
                            var seg = fullTranscript[i];
                            var newDuration = (seg.End - actualStart).TotalSeconds;
                            if (newDuration > 60.0) break;

                            var segText = seg.Text.TrimEnd();
                            if (segText.EndsWith(".") || segText.EndsWith("!") || segText.EndsWith("?"))
                            {
                                if (newDuration >= 15.0)
                                {
                                    actualEnd = seg.End;
                                    duration = newDuration;
                                    _logger.Log($"Extended short clip to reach minimum duration.");
                                    break;
                                }
                            }
                        }
                    }

                    // Final duration check
                    double minLimitSeconds = autoLength ? AutoMinAcceptSeconds : (minSeconds * 0.6);
                    if (duration < minLimitSeconds)
                    {
                        _logger.Log($"Rejecting highlight: duration ({duration}s) is too short.");
                        continue;
                    }

                    // Build transcript text
                    var textSegments = fullTranscript.Where(s => s.Start >= actualStart && s.End <= actualEnd).Select(s => s.Text);
                    string text = string.Join(" ", textSegments).Trim();

                    if (autoLength)
                    {
                        _logger.Log($"AI clip [{actualStart:hh\\:mm\\:ss} - {actualEnd:hh\\:mm\\:ss}] ({duration:F1}s) score {h.Score:F2}: {h.Reason}");
                    }

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
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (GroqApiException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Log($"LLM AI Clip finding chunk failed: {ex.Message}. Skipping chunk.");
                _failedChunks++;
                return new List<ClipCandidate>();
            }
        }

        private static string? TryGetGroqErrorCode(string body)
        {
            try
            {
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("error", out var errorEl) &&
                    errorEl.TryGetProperty("code", out var codeEl) &&
                    codeEl.ValueKind == JsonValueKind.String)
                {
                    return codeEl.GetString();
                }
            }
            catch (JsonException)
            {
            }
            return null;
        }

        private static string BuildPayload(string promptText, bool strictSchema)
        {
            object responseFormat;
            if (strictSchema)
            {
                responseFormat = new
                {
                    type = "json_schema",
                    json_schema = new
                    {
                        name = "highlights_schema",
                        strict = true,
                        schema = new
                        {
                            type = "object",
                            properties = new
                            {
                                highlights = new
                                {
                                    type = "array",
                                    items = new
                                    {
                                        type = "object",
                                        properties = new
                                        {
                                            startSegment = new { type = "integer" },
                                            endSegment = new { type = "integer" },
                                            score = new { type = "number" },
                                            reason = new { type = "string" }
                                        },
                                        required = new[] { "startSegment", "endSegment", "score", "reason" },
                                        additionalProperties = false
                                    }
                                }
                            },
                            required = new[] { "highlights" },
                            additionalProperties = false
                        }
                    }
                };
            }
            else
            {
                responseFormat = new { type = "json_object" };
            }

            var payload = new
            {
                model = GroqConfig.ModelId,
                max_completion_tokens = 8192,
                reasoning_effort = "low",
                messages = new[]
                {
                    new { role = "system", content = "You must respond ONLY with valid JSON. Your entire output must strictly match the requested JSON schema. Do not include markdown formatting, backticks, or conversational text." },
                    new { role = "user", content = promptText }
                },
                response_format = responseFormat
            };

            return JsonSerializer.Serialize(payload);
        }

        private class GroqApiException : Exception
        {
            public GroqApiException(string message) : base(message) { }
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
