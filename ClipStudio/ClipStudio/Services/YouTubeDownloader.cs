using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace ClipStudio.Services
{
    public class YouTubeDownloader
    {
        private readonly IActivityLogger _logger;
        private readonly string _ytDlpPath;

        public YouTubeDownloader(IActivityLogger logger)
        {
            _logger = logger;
            _ytDlpPath = Path.Combine(AppContext.BaseDirectory, "Binaries", "yt-dlp.exe");
        }

        public async Task<string> DownloadVideoAsync(string url, string outputFolder, string quality, IProgress<int> progress, CancellationToken cancellationToken)
        {
            if (!File.Exists(_ytDlpPath))
            {
                throw new FileNotFoundException($"yt-dlp.exe not found at {_ytDlpPath}. Please place it in the Binaries folder.");
            }

            // Using quality parameter:
            // "1080p" -> "bestvideo[height<=1080]+bestaudio/best[height<=1080]"
            string format = $"bestvideo[height<={quality.Replace("p", "")}]+bestaudio/best[height<={quality.Replace("p", "")}]";

            string finalFilePattern = Path.Combine(outputFolder, "%(title)s.%(ext)s");
            string arguments = $"--no-playlist --newline -f \"{format}\" -o \"{finalFilePattern}\" \"{url}\" --ffmpeg-location \"{Path.Combine(AppContext.BaseDirectory, "Binaries")}\"";

            string? finalFilePath = null;
            Regex progressRegex = new Regex(@"\[download\]\s+(?<percent>\d+\.\d)%");
            Regex destinationRegex = new Regex(@"\[download\] Destination: (?<path>.*)");
            Regex mergingRegex = new Regex(@"\[Merger\] Merging formats into ""(?<path>.*)""");
            Regex alreadyDownloadedRegex = new Regex(@"\[download\] (?<path>.*) has already been downloaded");

            _logger.Log($"Starting yt-dlp with arguments: {arguments}");

            int exitCode = await ProcessUtils.RunProcessAsync(_ytDlpPath, arguments, outputFolder, line =>
            {
                var progressMatch = progressRegex.Match(line);
                if (progressMatch.Success)
                {
                    if (double.TryParse(progressMatch.Groups["percent"].Value, out double percent))
                    {
                        progress.Report((int)percent);
                    }
                }

                var destMatch = destinationRegex.Match(line);
                if (destMatch.Success)
                {
                    finalFilePath = destMatch.Groups["path"].Value.Trim();
                }

                var mergeMatch = mergingRegex.Match(line);
                if (mergeMatch.Success)
                {
                    finalFilePath = mergeMatch.Groups["path"].Value.Trim();
                }

                var alreadyMatch = alreadyDownloadedRegex.Match(line);
                if (alreadyMatch.Success)
                {
                    finalFilePath = alreadyMatch.Groups["path"].Value.Trim();
                }

            }, cancellationToken);

            if (exitCode != 0)
            {
                throw new Exception($"yt-dlp failed with exit code {exitCode}");
            }

            if (string.IsNullOrEmpty(finalFilePath) || !File.Exists(finalFilePath))
            {
                // Fallback attempt to find the file if parsing failed.
                // yt-dlp might not have printed the exact line we expect.
                var files = Directory.GetFiles(outputFolder);
                if (files.Length > 0)
                {
                    Array.Sort(files, (a, b) => File.GetCreationTime(b).CompareTo(File.GetCreationTime(a)));
                    finalFilePath = files[0];
                    _logger.Log($"Warning: Using most recently created file as fallback: {finalFilePath}");
                }
                else
                {
                    throw new FileNotFoundException("Could not determine the downloaded file path.");
                }
            }

            return finalFilePath;
        }
    }
}
