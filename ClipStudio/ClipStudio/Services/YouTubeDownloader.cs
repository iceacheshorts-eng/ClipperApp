using System;
using System.Collections.Generic;
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

            if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Invalid URL provided. Please provide a valid HTTP or HTTPS URL.", nameof(url));
            }

            // Using quality parameter:
            // "1080p" -> "bestvideo[height<=1080]+bestaudio/best[height<=1080]"
            string format = $"bestvideo[height<={quality.Replace("p", "")}]+bestaudio/best[height<={quality.Replace("p", "")}]";

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _ytDlpPath,
                WorkingDirectory = outputFolder
            };

            startInfo.ArgumentList.Add("--no-playlist");
            startInfo.ArgumentList.Add("--newline");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add(format);
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(Path.Combine(outputFolder, "%(id)s.%(ext)s"));
            startInfo.ArgumentList.Add("--merge-output-format");
            startInfo.ArgumentList.Add("mp4");
            startInfo.ArgumentList.Add("-S");
            startInfo.ArgumentList.Add("vcodec:h264,res,acodec:m4a");
            startInfo.ArgumentList.Add("--print");
            startInfo.ArgumentList.Add("after_move:CLIPSTUDIO_PATH=%(filepath)s");
            startInfo.ArgumentList.Add("--progress");
            startInfo.ArgumentList.Add("--ffmpeg-location");
            startInfo.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "Binaries"));
            startInfo.ArgumentList.Add("--");
            startInfo.ArgumentList.Add(url.Trim());

            string? finalFilePath = null;
            Regex progressRegex = new Regex(@"\[download\]\s+(?<percent>\d+\.\d)%");
            var recentLines = new Queue<string>();
            var recentLinesLock = new object();

            _logger.Log($"Starting yt-dlp with arguments: {string.Join(" ", startInfo.ArgumentList)}");

            int exitCode = await ProcessUtils.RunProcessAsync(startInfo, line =>
            {
                lock (recentLinesLock)
                {
                    recentLines.Enqueue(line);
                    if (recentLines.Count > 10)
                    {
                        recentLines.Dequeue();
                    }
                }

                var progressMatch = progressRegex.Match(line);
                if (progressMatch.Success)
                {
                    if (double.TryParse(progressMatch.Groups["percent"].Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double percent))
                    {
                        progress.Report((int)percent);
                    }
                }

                if (line.Trim().StartsWith("CLIPSTUDIO_PATH="))
                {
                    finalFilePath = line.Trim().Substring("CLIPSTUDIO_PATH=".Length);
                }

            }, cancellationToken);

            if (exitCode != 0)
            {
                string recentLinesStr;
                lock (recentLinesLock)
                {
                    recentLinesStr = string.Join("\n", recentLines);
                }
                throw new Exception($"yt-dlp failed with exit code {exitCode}. Last output:\n{recentLinesStr}");
            }

            if (string.IsNullOrEmpty(finalFilePath) || !File.Exists(finalFilePath))
            {
                string recentLinesStr;
                lock (recentLinesLock)
                {
                    recentLinesStr = string.Join("\n", recentLines);
                }
                throw new InvalidOperationException($"yt-dlp finished but reported no output file. Last output:\n{recentLinesStr}");
            }

            return finalFilePath;
        }
    }
}
