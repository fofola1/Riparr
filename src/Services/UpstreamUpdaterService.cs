using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Riparr.Config;

namespace Riparr.Services
{
    public class UpstreamUpdaterService : BackgroundService
    {
        private readonly ILogger<UpstreamUpdaterService> _logger;
        private readonly HttpClient _httpClient;

        public DateTimeOffset? LastCheckTime { get; private set; }
        public string AniCliVersion { get; private set; } = "Unknown";
        public string YtDlpVersion { get; private set; } = "Unknown";
        public string LastError { get; private set; } = string.Empty;

        public UpstreamUpdaterService(ILogger<UpstreamUpdaterService> logger)
        {
            _logger = logger;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) RiparrAutoUpdater/1.0");
            _httpClient.Timeout = TimeSpan.FromSeconds(30);
        }

        public async Task TriggerUpdateAsync(CancellationToken cancellationToken = default)
        {
            _logger.LogInformation("[UpstreamUpdater] Manual update check triggered.");
            await PerformUpdateCheckAsync(cancellationToken);
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("[UpstreamUpdater] Background service started.");
            
            // Perform initial check shortly after startup
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            await PerformUpdateCheckAsync(stoppingToken);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    // Periodically check every 6 hours
                    await Task.Delay(TimeSpan.FromHours(6), stoppingToken);
                    await PerformUpdateCheckAsync(stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[UpstreamUpdater] Unexpected error during scheduled update.");
                    LastError = ex.Message;
                }
            }
        }

        public async Task PerformUpdateCheckAsync(CancellationToken cancellationToken = default)
        {
            LastCheckTime = DateTimeOffset.UtcNow;
            LastError = string.Empty;

            try
            {
                AppConfig.EnsureDirectoriesExist();
                await UpdateAniCliAsync(cancellationToken);
                await UpdateYtDlpAsync(cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UpstreamUpdater] Error checking for upstream updates.");
                LastError = ex.Message;
            }
        }

        private async Task UpdateAniCliAsync(CancellationToken cancellationToken)
        {
            try
            {
                string targetPath = Path.Combine(AppConfig.ToolsFolder, "ani-cli");
                string remoteUrl = "https://raw.githubusercontent.com/pystardust/ani-cli/master/ani-cli";

                var response = await _httpClient.GetAsync(remoteUrl, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[UpstreamUpdater] Failed to fetch remote ani-cli (HTTP {StatusCode})", response.StatusCode);
                    return;
                }

                string remoteContent = await response.Content.ReadAsStringAsync(cancellationToken);
                if (string.IsNullOrWhiteSpace(remoteContent) || (!remoteContent.StartsWith("#!/bin/sh") && !remoteContent.StartsWith("#!/usr/bin/env")))
                {
                    _logger.LogWarning("[UpstreamUpdater] Remote ani-cli content validation failed.");
                    return;
                }

                string remoteVersion = ExtractVersion(remoteContent, "version_number=\"") ?? "Unknown";

                var utf8WithoutBom = new UTF8Encoding(false);
                byte[] remoteBytes = utf8WithoutBom.GetBytes(remoteContent);

                bool needsWrite = true;
                if (File.Exists(targetPath))
                {
                    byte[] localBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken);
                    string localHash = ComputeSha256Bytes(localBytes);
                    string remoteHash = ComputeSha256Bytes(remoteBytes);
                    if (string.Equals(localHash, remoteHash, StringComparison.Ordinal))
                    {
                        needsWrite = false;
                    }
                }

                if (needsWrite)
                {
                    await File.WriteAllBytesAsync(targetPath, remoteBytes, cancellationToken);
                    SetExecutablePermissions(targetPath);
                    _logger.LogInformation("[UpstreamUpdater] Successfully updated ani-cli to version '{Version}' at {Path}", remoteVersion, targetPath);
                }

                AniCliVersion = remoteVersion;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UpstreamUpdater] Failed to update ani-cli.");
            }
        }

        private async Task UpdateYtDlpAsync(CancellationToken cancellationToken)
        {
            try
            {
                string targetPath = Path.Combine(AppConfig.ToolsFolder, "yt-dlp");
                string releaseUrl = "https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp";

                var response = await _httpClient.GetAsync(releaseUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning("[UpstreamUpdater] Failed to fetch remote yt-dlp binary (HTTP {StatusCode})", response.StatusCode);
                    return;
                }

                byte[] remoteBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
                if (remoteBytes.Length < 1000)
                {
                    _logger.LogWarning("[UpstreamUpdater] Remote yt-dlp binary validation failed (size too small).");
                    return;
                }

                bool needsWrite = true;
                if (File.Exists(targetPath))
                {
                    byte[] localBytes = await File.ReadAllBytesAsync(targetPath, cancellationToken);
                    string localHash = ComputeSha256Bytes(localBytes);
                    string remoteHash = ComputeSha256Bytes(remoteBytes);
                    if (string.Equals(localHash, remoteHash, StringComparison.Ordinal))
                    {
                        needsWrite = false;
                    }
                }

                if (needsWrite)
                {
                    await File.WriteAllBytesAsync(targetPath, remoteBytes, cancellationToken);
                    SetExecutablePermissions(targetPath);
                    _logger.LogInformation("[UpstreamUpdater] Successfully updated yt-dlp binary at {Path}", targetPath);
                }

                YtDlpVersion = "Latest";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[UpstreamUpdater] Failed to update yt-dlp.");
            }
        }

        private static string? ExtractVersion(string content, string prefix)
        {
            int idx = content.IndexOf(prefix, StringComparison.Ordinal);
            if (idx >= 0)
            {
                int start = idx + prefix.Length;
                int end = content.IndexOf('"', start);
                if (end > start)
                {
                    return content.Substring(start, end - start);
                }
            }
            return null;
        }

        private static string ComputeSha256(string input)
        {
            byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
            return Convert.ToHexString(bytes);
        }

        private static string ComputeSha256Bytes(byte[] bytes)
        {
            byte[] hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash);
        }

        private static void SetExecutablePermissions(string path)
        {
            try
            {
                if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
                {
                    File.SetUnixFileMode(path, 
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
            }
            catch
            {
                try
                {
                    System.Diagnostics.Process.Start("chmod", $"+x \"{path}\"")?.WaitForExit();
                }
                catch { }
            }
        }
    }
}
