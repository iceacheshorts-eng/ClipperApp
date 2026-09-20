using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ClipStudio.Services
{
    public static class FfmpegCapabilities
    {
        private static readonly ConcurrentDictionary<string, bool> _filterCache = new();

        public static async Task<bool> HasFilterAsync(string ffmpegPath, string filterName, CancellationToken ct)
        {
            if (_filterCache.TryGetValue(filterName, out bool hasFilter))
            {
                return hasFilter;
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                WorkingDirectory = Environment.CurrentDirectory
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-filters");

            bool found = false;

            try
            {
                int exitCode = await ProcessUtils.RunProcessAsync(startInfo, line =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;

                    var tokens = line.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    // Filter lines look like: "T.. ass V->V Render ASS subtitles..."
                    // Skip header lines by checking if we have enough tokens and the second token is the filter name
                    if (tokens.Length >= 2 && tokens[1] == filterName)
                    {
                        found = true;
                    }
                }, ct);

                if (exitCode == 0)
                {
                    _filterCache.TryAdd(filterName, found);
                }

                return found;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return false;
            }
        }
    }
}
