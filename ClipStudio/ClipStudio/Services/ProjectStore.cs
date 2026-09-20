using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Globalization;
using ClipStudio.Models;
using System.Linq;

namespace ClipStudio.Services
{
    public static class ProjectStore
    {
        private const int SchemaVersion = 1;

        public static string GetProjectsRoot()
        {
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClipStudio", "projects");
        }

        public static string? GetSourceKey(string videoPath, bool isDownloaded)
        {
            try
            {
                if (isDownloaded)
                {
                    string name = Path.GetFileNameWithoutExtension(videoPath);
                    if (Regex.IsMatch(name, "^[A-Za-z0-9_-]{1,64}$"))
                    {
                        return "yt_" + name;
                    }
                }

                var fileInfo = new FileInfo(videoPath);
                string hashInput = Path.GetFullPath(videoPath).ToLowerInvariant() + "|" + fileInfo.Length.ToString(CultureInfo.InvariantCulture) + "|" + fileInfo.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);

                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(hashInput));
                    string hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    return "file_" + hashHex.Substring(0, 16);
                }
            }
            catch
            {
                return null;
            }
        }

        public static TranscriptionResult? TryLoadTranscript(string sourceKey, string modelFileName, long modelFileSize, bool fillerPrompt, bool wordLevel)
        {
            try
            {
                string dirPath = Path.Combine(GetProjectsRoot(), sourceKey);
                string filePath = Path.Combine(dirPath, "transcript.json");

                if (!File.Exists(filePath))
                {
                    return null;
                }

                string json = File.ReadAllText(filePath);
                var cacheFile = JsonSerializer.Deserialize<TranscriptCacheFile>(json);

                if (cacheFile == null) return null;

                if (cacheFile.SchemaVersion == SchemaVersion &&
                    cacheFile.ModelFileName == modelFileName &&
                    cacheFile.ModelFileSize == modelFileSize &&
                    cacheFile.FillerPrompt == fillerPrompt &&
                    cacheFile.WordLevel == wordLevel &&
                    cacheFile.Transcription != null &&
                    cacheFile.Transcription.Words.Any() &&
                    cacheFile.Transcription.Segments.Any())
                {
                    return cacheFile.Transcription;
                }
            }
            catch
            {
            }

            return null;
        }

        public static string TranscriptFingerprint(System.Collections.Generic.IReadOnlyList<TranscriptSegment> transcript)
        {
            try
            {
                var sb = new StringBuilder();
                foreach (var seg in transcript)
                {
                    sb.Append($"{seg.Index}|{seg.Start.Ticks}|{seg.End.Ticks}|{seg.Text}\n");
                }

                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
                    string hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                    return hashHex.Substring(0, 16);
                }
            }
            catch
            {
                return "";
            }
        }

        public static HighlightsCacheFile? TryLoadHighlights(string sourceKey, string configKey)
        {
            try
            {
                string hashHex;
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(configKey));
                    hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
                string configPrefix = hashHex.Substring(0, 12);

                string dirPath = Path.Combine(GetProjectsRoot(), sourceKey);
                string filePath = Path.Combine(dirPath, $"highlights_{configPrefix}.json");

                if (!File.Exists(filePath))
                {
                    return null;
                }

                string json = File.ReadAllText(filePath);
                var cacheFile = JsonSerializer.Deserialize<HighlightsCacheFile>(json);

                if (cacheFile == null) return null;

                if (cacheFile.SchemaVersion == SchemaVersion &&
                    cacheFile.ConfigKey == configKey)
                {
                    return cacheFile;
                }
            }
            catch
            {
            }

            return null;
        }

        public static void SaveHighlights(string sourceKey, string configKey, int perChunkCount, string modelId, System.Collections.Generic.IEnumerable<ClipCandidate> clips)
        {
            try
            {
                string hashHex;
                using (var sha256 = SHA256.Create())
                {
                    byte[] hashBytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(configKey));
                    hashHex = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant();
                }
                string configPrefix = hashHex.Substring(0, 12);

                string dirPath = Path.Combine(GetProjectsRoot(), sourceKey);
                Directory.CreateDirectory(dirPath);

                string filePath = Path.Combine(dirPath, $"highlights_{configPrefix}.json");
                string tmpPath = filePath + ".tmp";

                var cacheFile = new HighlightsCacheFile
                {
                    SchemaVersion = SchemaVersion,
                    SourceKey = sourceKey,
                    ConfigKey = configKey,
                    PerChunkCount = perChunkCount,
                    ModelId = modelId,
                    SavedUtc = DateTime.UtcNow,
                    Clips = clips.Select(c => new SavedClip
                    {
                        Start = c.StartTime,
                        End = c.EndTime,
                        Score = c.Score,
                        Reason = c.Reason,
                        Transcript = c.Transcript
                    }).ToList()
                };

                string json = JsonSerializer.Serialize(cacheFile);
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, filePath, true);
            }
            catch
            {
            }
        }

        public static void SaveTranscript(string sourceKey, string modelFileName, long modelFileSize, bool fillerPrompt, bool wordLevel, TranscriptionResult result)
        {
            try
            {
                string dirPath = Path.Combine(GetProjectsRoot(), sourceKey);
                Directory.CreateDirectory(dirPath);

                string filePath = Path.Combine(dirPath, "transcript.json");
                string tmpPath = filePath + ".tmp";

                var cacheFile = new TranscriptCacheFile
                {
                    SchemaVersion = SchemaVersion,
                    SourceKey = sourceKey,
                    ModelFileName = modelFileName,
                    ModelFileSize = modelFileSize,
                    FillerPrompt = fillerPrompt,
                    WordLevel = wordLevel,
                    SavedUtc = DateTime.UtcNow,
                    Transcription = result
                };

                string json = JsonSerializer.Serialize(cacheFile);
                File.WriteAllText(tmpPath, json);
                File.Move(tmpPath, filePath, true);
            }
            catch
            {
            }
        }

        public static bool ClearAll()
        {
            try
            {
                string dirPath = GetProjectsRoot();
                if (Directory.Exists(dirPath))
                {
                    Directory.Delete(dirPath, true);
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static void CleanupOld(TimeSpan maxAge)
        {
            try
            {
                string root = GetProjectsRoot();
                if (!Directory.Exists(root)) return;

                var threshold = DateTime.UtcNow - maxAge;

                foreach (var dir in Directory.GetDirectories(root))
                {
                    var files = Directory.GetFiles(dir);
                    if (files.Length == 0) continue;

                    var newest = files.Select(f => new FileInfo(f).LastWriteTimeUtc).Max();

                    if (newest < threshold)
                    {
                        Directory.Delete(dir, true);
                    }
                }
            }
            catch
            {
            }
        }
    }
}
