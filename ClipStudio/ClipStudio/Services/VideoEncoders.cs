using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClipStudio.Services
{
    public static class VideoEncoders
    {
        private static bool? _nvencSupported;
        private static bool? _qsvSupported;
        private static bool? _amfSupported;

        private static string GetFfmpegPath()
        {
            return Path.Combine(AppContext.BaseDirectory, "Binaries", "ffmpeg.exe");
        }

        public static async Task<bool> IsNvencSupportedAsync(CancellationToken token)
        {
            if (_nvencSupported.HasValue) return _nvencSupported.Value;
            _nvencSupported = await TestEncoderAsync("-f lavfi -i color=c=black:s=1920x1080 -t 1 -c:v h264_nvenc -preset p5 -tune hq -rc vbr -cq 23 -b:v 0 -f null -", token);
            return _nvencSupported.Value;
        }

        public static async Task<bool> IsQsvSupportedAsync(CancellationToken token)
        {
            if (_qsvSupported.HasValue) return _qsvSupported.Value;
            _qsvSupported = await TestEncoderAsync("-f lavfi -i color=c=black:s=1920x1080 -t 1 -c:v h264_qsv -global_quality 23 -preset medium -f null -", token);
            return _qsvSupported.Value;
        }

        public static async Task<bool> IsAmfSupportedAsync(CancellationToken token)
        {
            if (_amfSupported.HasValue) return _amfSupported.Value;
            _amfSupported = await TestEncoderAsync("-f lavfi -i color=c=black:s=1920x1080 -t 1 -c:v h264_amf -quality balanced -rc cqp -qp_i 23 -qp_p 23 -qp_b 23 -f null -", token);
            return _amfSupported.Value;
        }

        public static async Task<string> GetAutoEncoderAsync(CancellationToken token)
        {
            if (await IsNvencSupportedAsync(token)) return "NVENC";
            if (await IsQsvSupportedAsync(token)) return "QSV";
            if (await IsAmfSupportedAsync(token)) return "AMF";
            return "CPU";
        }

        private static async Task<bool> TestEncoderAsync(string args, CancellationToken token)
        {
            string ffmpegPath = GetFfmpegPath();
            if (!File.Exists(ffmpegPath)) return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpegPath,
                WorkingDirectory = TempPaths.GetTempDir()
            };

            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var part in parts)
            {
                startInfo.ArgumentList.Add(part);
            }

            try
            {
                int exitCode = await ProcessUtils.RunProcessAsync(startInfo, _ => { }, token);
                return exitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }
}
