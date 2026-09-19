using System;
using System.IO;
using System.Linq;

namespace ClipStudio.Services
{
    public static class TempPaths
    {
        private static string? _cachedTempDir;

        private static bool IsAscii(string path)
        {
            return path.All(c => c <= 127);
        }

        public static string GetTempDir()
        {
            if (_cachedTempDir != null)
            {
                return _cachedTempDir;
            }

            string defaultTemp = Path.GetTempPath();
            if (IsAscii(defaultTemp))
            {
                _cachedTempDir = defaultTemp;
                return _cachedTempDir;
            }

            string fallbackPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ClipStudio", "tmp");
            if (IsAscii(fallbackPath))
            {
                try
                {
                    if (!Directory.Exists(fallbackPath))
                    {
                        Directory.CreateDirectory(fallbackPath);
                    }
                    _cachedTempDir = fallbackPath;
                    return _cachedTempDir;
                }
                catch
                {
                    // Fallback to default if creation fails
                }
            }

            _cachedTempDir = defaultTemp;
            return _cachedTempDir;
        }

        public static string NewTempFile(string extension)
        {
            if (!extension.StartsWith("."))
            {
                extension = "." + extension;
            }
            string filename = $"clipstudio_{Guid.NewGuid():N}{extension}";
            return Path.Combine(GetTempDir(), filename);
        }

        public static void CleanupStale()
        {
            try
            {
                string tempDir = GetTempDir();
                if (!Directory.Exists(tempDir))
                    return;

                var files = Directory.GetFiles(tempDir, "clipstudio_*");
                DateTime threshold = DateTime.Now.AddHours(-24);

                foreach (var file in files)
                {
                    try
                    {
                        if (File.GetLastWriteTime(file) < threshold)
                        {
                            File.Delete(file);
                        }
                    }
                    catch
                    {
                        // Swallow per-file exceptions
                    }
                }
            }
            catch
            {
                // Swallow all exceptions for the cleanup process
            }
        }
    }
}
