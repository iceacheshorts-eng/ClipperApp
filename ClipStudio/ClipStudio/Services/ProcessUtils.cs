using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace ClipStudio.Services
{
    public static class ProcessUtils
    {
        public static async Task<int> RunProcessAsync(
            ProcessStartInfo startInfo,
            Action<string> onOutputLine,
            CancellationToken cancellationToken)
        {
            return await RunProcessCoreAsync(startInfo, onOutputLine, cancellationToken);
        }

        private static async Task<int> RunProcessCoreAsync(
            ProcessStartInfo startInfo,
            Action<string> onOutputLine,
            CancellationToken cancellationToken)
        {
            startInfo.RedirectStandardOutput = true;
            startInfo.RedirectStandardError = true;
            startInfo.UseShellExecute = false;
            startInfo.CreateNoWindow = true;

            using var process = new Process { StartInfo = startInfo };

            process.EnableRaisingEvents = true;

            process.OutputDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    onOutputLine(args.Data);
                }
            };
            process.ErrorDataReceived += (sender, args) =>
            {
                if (args.Data != null)
                {
                    onOutputLine(args.Data); // We usually want to log errors too
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException($"Failed to start process: {startInfo.FileName}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            try
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Ignore exceptions during kill
                }
                throw;
            }

            return process.ExitCode;
        }
    }
}
