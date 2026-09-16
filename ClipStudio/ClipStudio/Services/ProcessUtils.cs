using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Management;

namespace ClipStudio.Services
{
    public static class ProcessUtils
    {
        public static async Task<int> RunProcessAsync(
            string fileName,
            string arguments,
            string workingDirectory,
            Action<string> onOutputLine,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };

            var tcs = new TaskCompletionSource<int>();

            process.EnableRaisingEvents = true;
            process.Exited += (sender, args) => tcs.TrySetResult(process.ExitCode);

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
                throw new InvalidOperationException($"Failed to start process: {fileName}");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        KillProcessTree(process.Id);
                    }
                    tcs.TrySetCanceled();
                }
                catch
                {
                    // Ignore exceptions during kill
                }
            });

            return await tcs.Task;
        }

        public static void KillProcessTree(int processId)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    $"Select * From Win32_Process Where ParentProcessID={processId}");
                var moc = searcher.Get();
                foreach (var mo in moc)
                {
                    KillProcessTree(Convert.ToInt32(mo["ProcessID"]));
                }
                var proc = Process.GetProcessById(processId);
                proc.Kill();
            }
            catch
            {
                // Process might have already exited.
            }
        }
    }
}
