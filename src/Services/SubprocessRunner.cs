using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Riparr.Models;
using Riparr.Config;

namespace Riparr.Services
{
    public class SubprocessRunner
    {
        private static readonly Regex PercentageRegex = new(@"\b(\d+(?:\.\d+)?)%", RegexOptions.Compiled);
        private static readonly Regex SpeedRegex = new(@"\b(\d+(?:\.\d+)?\s*(?:[KMGT]i?B/s|[KMGT]B/s|[kmgt]/s))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private static readonly Regex SizeRegex = new(@"(?:of|/)\s*(\d+(?:\.\d+)?\s*(?:[KMGT]i?B|[KMGT]B))\b", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public async Task<bool> RunDownloadAsync(
            DownloadJob job, 
            Action<double, string, string> onProgressUpdate, 
            CancellationToken cancellationToken)
        {
            AppConfig.EnsureDirectoriesExist();
            
            bool isAniCli = job.StreamUrl.StartsWith("ani-cli:", StringComparison.OrdinalIgnoreCase) || 
                            string.IsNullOrEmpty(job.StreamUrl) || 
                            job.StreamUrl.Equals("ani-cli", StringComparison.OrdinalIgnoreCase);

            string processName;
            string arguments;
            string workingDirectory = AppConfig.IncompleteFolder;

            if (isAniCli)
            {
                processName = "/usr/bin/ani-cli";
                // ani-cli -d (download) -S 1 (first search result) -e <episode> "<title>"
                arguments = $"-d -S 1 -e {job.Episode} \"{job.Title}\"";
            }
            else
            {
                processName = "/usr/bin/yt-dlp";
                // Download using yt-dlp to temp location in incomplete folder
                arguments = $"-o \"{AppConfig.IncompleteFolder}/{job.Filename}.temp\" \"{job.StreamUrl}\"";
            }

            var startInfo = new ProcessStartInfo
            {
                FileName = processName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = workingDirectory
            };

            // Set environment variable for ani-cli download directory
            if (isAniCli)
            {
                startInfo.EnvironmentVariables["ANI_CLI_DOWNLOAD_DIR"] = AppConfig.IncompleteFolder;
            }

            using var process = new Process();
            process.StartInfo = startInfo;

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                job.ErrorMessage = $"Failed to start process {processName}: {ex.Message}";
                return false;
            }

            var stderrBuilder = new StringBuilder();
            var stdoutTask = ReadAndParseStreamAsync(process.StandardOutput, onProgressUpdate, null, cancellationToken);
            var stderrTask = ReadAndParseStreamAsync(process.StandardError, onProgressUpdate, stderrBuilder, cancellationToken);

            var processExitTask = process.WaitForExitAsync(cancellationToken);
            
            await Task.WhenAll(stdoutTask, stderrTask, processExitTask);

            if (process.ExitCode != 0)
            {
                var stderrContent = stderrBuilder.ToString().Trim();
                job.ErrorMessage = $"Process exited with code {process.ExitCode}. Stderr: {stderrContent}";
                return false;
            }

            return true;
        }

        private async Task ReadAndParseStreamAsync(
            StreamReader reader, 
            Action<double, string, string> onProgressUpdate, 
            StringBuilder? errorAccumulator,
            CancellationToken cancellationToken)
        {
            var buffer = new char[4096];
            var lineBuilder = new StringBuilder();

            while (!cancellationToken.IsCancellationRequested)
            {
                int read = await reader.ReadAsync(buffer, 0, buffer.Length);
                if (read == 0) break; // EOF

                for (int i = 0; i < read; i++)
                {
                    char c = buffer[i];
                    if (c == '\r' || c == '\n')
                    {
                        if (lineBuilder.Length > 0)
                        {
                            var line = lineBuilder.ToString();
                            if (errorAccumulator != null)
                            {
                                errorAccumulator.AppendLine(line);
                            }
                            ParseLine(line, onProgressUpdate);
                            lineBuilder.Clear();
                        }
                    }
                    else
                    {
                        lineBuilder.Append(c);
                    }
                }
            }

            if (lineBuilder.Length > 0)
            {
                var line = lineBuilder.ToString();
                if (errorAccumulator != null)
                {
                    errorAccumulator.AppendLine(line);
                }
                ParseLine(line, onProgressUpdate);
            }
        }

        private void ParseLine(string line, Action<double, string, string> onProgressUpdate)
        {
            if (string.IsNullOrWhiteSpace(line)) return;

            double progress = -1;
            string speed = string.Empty;
            string size = string.Empty;

            var percentageMatch = PercentageRegex.Match(line);
            if (percentageMatch.Success && double.TryParse(percentageMatch.Groups[1].Value, out double parsedProgress))
            {
                progress = parsedProgress;
            }

            var speedMatch = SpeedRegex.Match(line);
            if (speedMatch.Success)
            {
                speed = speedMatch.Groups[1].Value.Trim();
            }

            var sizeMatch = SizeRegex.Match(line);
            if (sizeMatch.Success)
            {
                size = sizeMatch.Groups[1].Value.Trim();
            }

            if (progress >= 0 || !string.IsNullOrEmpty(speed) || !string.IsNullOrEmpty(size))
            {
                onProgressUpdate(progress, speed, size);
            }
        }
    }
}
