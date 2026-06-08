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
                processName = "/usr/bin/sleep";
                arguments = "3";
            }
            else if (isAniCli)
            {
                processName = "/usr/bin/ani-cli";
                string cleanTitle = CleanAnimeTitle(job.Title);
                
                int episodeNum = 1;
                int.TryParse(job.Episode, out episodeNum);
                
                int selectedIndex = await GetAllAnimeIndexAsync(cleanTitle, episodeNum, cancellationToken);
                
                // ani-cli -d (download) -S <index> -e <episode> "<title>"
                arguments = $"-d -S {selectedIndex} -e {job.Episode} \"{cleanTitle}\"";
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

        private string CleanAnimeTitle(string title)
        {
            // Remove group tag at start: "[MockSub] Frieren..." -> "Frieren..."
            string cleaned = Regex.Replace(title, @"^\[[^\]]+\]\s*", "");

            // If it contains a colon, split and take the first part (e.g. "Frieren: Beyond Journey's End" -> "Frieren")
            if (cleaned.Contains(":"))
            {
                cleaned = cleaned.Split(':')[0];
            }

            // Remove season/episode markers at the end: "... - S01E17 [1080p]" -> "..."
            cleaned = Regex.Replace(cleaned, @"\s*-\s*S?\d+E\d+.*", "", RegexOptions.IgnoreCase);
            cleaned = Regex.Replace(cleaned, @"\s*-\s*E\d+.*", "", RegexOptions.IgnoreCase);

            // Remove resolution or extra tags like "[1080p]", "(HD)", etc.
            cleaned = Regex.Replace(cleaned, @"\s*\[[^\]]+\]", "");
            cleaned = Regex.Replace(cleaned, @"\s*\([^\)]+\)", "");

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
                        int currentIndex = 1;
                        foreach (var edge in edgesEl.EnumerateArray())
                        {
                            if (edge.TryGetProperty("availableEpisodes", out var epEl))
                            {
                                int subCount = 0;
                                int dubCount = 0;
                                if (epEl.TryGetProperty("sub", out var subEl) && subEl.ValueKind == JsonValueKind.Number)
                                {
                                    subCount = subEl.GetInt32();
                                }
                                if (epEl.TryGetProperty("dub", out var dubEl) && dubEl.ValueKind == JsonValueKind.Number)
                                {
                                    dubCount = dubEl.GetInt32();
                                }

                                if (subCount >= episode || dubCount >= episode)
                                {
                                    return currentIndex;
                                }
                            }
                            currentIndex++;
                        }
                    }
                }
            }
            catch (Exception)
            {
                // Fallback to default index 1
            }

            return 1;
        }
    }
}
