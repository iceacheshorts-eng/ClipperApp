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
