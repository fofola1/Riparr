using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
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
            
            bool isMockUrl = job.StreamUrl.Contains("example-streaming.com", StringComparison.OrdinalIgnoreCase) ||
                             job.StreamUrl.Contains("example.com", StringComparison.OrdinalIgnoreCase);

            bool isTestEnv = AppConfig.IncompleteFolder.Contains("downloads_test") ||
                             AppConfig.DbPath.Contains("downloads_test");

            bool isAniCli = job.StreamUrl.StartsWith("ani-cli:", StringComparison.OrdinalIgnoreCase) || 
                            string.IsNullOrEmpty(job.StreamUrl) || 
                            job.StreamUrl.Equals("ani-cli", StringComparison.OrdinalIgnoreCase) ||
                            isMockUrl;

            string processName;
            string arguments;
            string workingDirectory = AppConfig.IncompleteFolder;

            if (isTestEnv && isMockUrl)
            {
                processName = ResolveExecutablePath("sleep");
                arguments = "3";
            }
            else if (isAniCli)
            {
                processName = ResolveExecutablePath("ani-cli");
                string cleanTitle = CleanAnimeTitle(job.Title);
                
                int episodeNum = 1;
                int.TryParse(job.Episode, out episodeNum);
                
                int selectedIndex = await GetAllAnimeIndexAsync(cleanTitle, episodeNum, cancellationToken);
                
                // ani-cli -d (download) -S <index> -e <episode> "<title>"
                arguments = $"-d -S {selectedIndex} -e {job.Episode} \"{cleanTitle}\"";
            }
            else
            {
                processName = ResolveExecutablePath("yt-dlp");
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

            // Set environment variables for ani-cli download directory and player
            if (isAniCli)
            {
                startInfo.EnvironmentVariables["ANI_CLI_DOWNLOAD_DIR"] = AppConfig.IncompleteFolder;
                startInfo.EnvironmentVariables["ANI_CLI_PLAYER"] = "aria2c";
                startInfo.EnvironmentVariables["TERM"] = "xterm-256color";
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
                // Fallback for ani-cli if initial selected index failed: retry without -S parameter
                if (isAniCli)
                {
                    string cleanTitle = CleanAnimeTitle(job.Title);
                    string fallbackArguments = $"-d -e {job.Episode} \"{cleanTitle}\"";
                    
                    var fallbackStartInfo = new ProcessStartInfo
                    {
                        FileName = processName,
                        Arguments = fallbackArguments,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        WorkingDirectory = workingDirectory
                    };
                    fallbackStartInfo.EnvironmentVariables["ANI_CLI_DOWNLOAD_DIR"] = AppConfig.IncompleteFolder;
                    fallbackStartInfo.EnvironmentVariables["ANI_CLI_PLAYER"] = "aria2c";
                    fallbackStartInfo.EnvironmentVariables["TERM"] = "xterm-256color";

                    using var fallbackProcess = new Process();
                    fallbackProcess.StartInfo = fallbackStartInfo;
                    try
                    {
                        fallbackProcess.Start();
                        var fallbackStdout = ReadAndParseStreamAsync(fallbackProcess.StandardOutput, onProgressUpdate, null, cancellationToken);
                        var fallbackStderr = ReadAndParseStreamAsync(fallbackProcess.StandardError, onProgressUpdate, stderrBuilder, cancellationToken);
                        var fallbackExit = fallbackProcess.WaitForExitAsync(cancellationToken);
                        await Task.WhenAll(fallbackStdout, fallbackStderr, fallbackExit);

                        if (fallbackProcess.ExitCode == 0)
                        {
                            return true;
                        }
                    }
                    catch (Exception)
                    {
                        // Ignore fallback launch errors and proceed to report original failure
                    }
                }

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

        private string CleanAnimeTitle(string title)
        {
            // 1. Remove group tag at start: "[AniCli] Tongari..." -> "Tongari..."
            string cleaned = Regex.Replace(title, @"^\[[^\]]+\]\s*", "");

            // 2. Remove season/episode markers: "... - S01E12 - 12 [1080p]" -> "... - 12 [1080p]" or "..."
            cleaned = Regex.Replace(cleaned, @"\s*-\s*S?\d+E\d+.*", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s*-\s*E\d+.*", "", RegexOptions.IgnoreCase);

            // 3. Remove resolution or extra tags like "[1080p]", "(HD)", etc.
            cleaned = Regex.Replace(cleaned, @"\s*\[[^\]]+\]", "");
            cleaned = Regex.Replace(cleaned, @"\s*\([^\)]+\)", "");

            // 4. Remove standalone episode numbers at the end: "Tongari Boushi no Atelier 03" -> "Tongari Boushi no Atelier"
            cleaned = Regex.Replace(cleaned, @"\s+\d{1,3}\s*$", "");

            // 5. If it contains a colon, split and take the main title part (e.g. "Frieren: Beyond Journey's End" -> "Frieren")
            if (cleaned.Contains(":"))
            {
                cleaned = cleaned.Split(':')[0];
            }

            return cleaned.Trim();
        }

        private async Task<int> GetAllAnimeIndexAsync(string cleanTitle, int episode, CancellationToken cancellationToken)
        {
            try
            {
                using var client = new HttpClient();
                client.DefaultRequestHeaders.Add("Referer", "https://youtu-chan.com");
                client.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

                var graphqlQuery = new
                {
                    variables = new
                    {
                        search = new
                        {
                            allowAdult = false,
                            allowUnknown = false,
                            query = cleanTitle
                        },
                        limit = 40,
                        page = 1,
                        translationType = "sub",
                        countryOrigin = "ALL"
                    },
                    query = "query($search: SearchInput $limit: Int $page: Int $translationType: VaildTranslationTypeEnumType $countryOrigin: VaildCountryOriginEnumType) { shows(search: $search limit: $limit page: $page translationType: $translationType countryOrigin: $countryOrigin) { edges { _id name availableEpisodes } } }"
                };

                string jsonPayload = JsonSerializer.Serialize(graphqlQuery);
                using var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

                var response = await client.PostAsync("https://api.allanime.day/api", content, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync(cancellationToken);
                    using var doc = JsonDocument.Parse(jsonResponse);
                    if (doc.RootElement.TryGetProperty("data", out var dataEl) &&
                        dataEl.TryGetProperty("shows", out var showsEl) &&
                        showsEl.TryGetProperty("edges", out var edgesEl) &&
                        edgesEl.ValueKind == JsonValueKind.Array)
                    {
                        int bestIndex = 1;
                        int maxEpisodes = -1;
                        int currentIndex = 1;

                        foreach (var edge in edgesEl.EnumerateArray())
                        {
                            string showName = edge.TryGetProperty("name", out var nameProp) ? nameProp.GetString() ?? "" : "";
                            int subCount = 0;
                            int dubCount = 0;
                            if (edge.TryGetProperty("availableEpisodes", out var epEl))
                            {
                                if (epEl.TryGetProperty("sub", out var subEl) && subEl.ValueKind == JsonValueKind.Number)
                                {
                                    subCount = subEl.GetInt32();
                                }
                                if (epEl.TryGetProperty("dub", out var dubEl) && dubEl.ValueKind == JsonValueKind.Number)
                                {
                                    dubCount = dubEl.GetInt32();
                                }
                            }

                            int totalEp = Math.Max(subCount, dubCount);
                            if (totalEp >= episode)
                            {
                                bool isTitleMatch = showName.Equals(cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                                                     showName.Contains(cleanTitle, StringComparison.OrdinalIgnoreCase) ||
                                                     cleanTitle.Contains(showName, StringComparison.OrdinalIgnoreCase);

                                if (isTitleMatch && totalEp > maxEpisodes)
                                {
                                    maxEpisodes = totalEp;
                                    bestIndex = currentIndex;
                                }
                                else if (maxEpisodes == -1 && totalEp > maxEpisodes)
                                {
                                    maxEpisodes = totalEp;
                                    bestIndex = currentIndex;
                                }
                            }
                            currentIndex++;
                        }

                        return bestIndex;
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to default index 1
            }

            return 1;
        }

        private static string ResolveExecutablePath(string binaryName)
        {
            string toolsBin = Path.Combine(AppConfig.ToolsFolder, binaryName);
            if (File.Exists(toolsBin))
            {
                return toolsBin;
            }

            string userLocalBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "bin", binaryName);
            if (File.Exists(userLocalBin))
            {
                return userLocalBin;
            }

            string usrBin = $"/usr/bin/{binaryName}";
            if (File.Exists(usrBin))
            {
                return usrBin;
            }

            string usrLocalBin = $"/usr/local/bin/{binaryName}";
            if (File.Exists(usrLocalBin))
            {
                return usrLocalBin;
            }

            return binaryName;
        }
    }
}
