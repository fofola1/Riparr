using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Riparr.Config;
using Riparr.Data;
using Riparr.Models;

namespace Riparr.Services
{
    public class DownloadManager : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<DownloadManager> _logger;
        private readonly ConcurrentDictionary<string, CancellationTokenSource> _activeTokens = new();

        public DownloadManager(IServiceProvider serviceProvider, ILogger<DownloadManager> logger)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
        }

        public bool CancelJob(string jobId)
        {
            if (_activeTokens.TryRemove(jobId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    _logger.LogInformation("Job {JobId} cancellation requested.", jobId);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error cancelling job {JobId}.", jobId);
                }
            }
            return false;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("DownloadManager Background Service started.");

            // Initialize database schema
            using (var scope = _serviceProvider.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<DownloadDbContext>();
                await db.Database.EnsureCreatedAsync(stoppingToken);
                
                // Reset any previously downloading jobs to Queued on startup (handling crashes/restarts)
                var activeJobs = db.Downloads.Where(x => x.Status == "Downloading").ToList();
                if (activeJobs.Any())
                {
                    foreach (var job in activeJobs)
                    {
                        job.Status = "Queued";
                        job.Speed = "0 B/s";
                        job.Progress = 0.0;
                    }
                    await db.SaveChangesAsync(stoppingToken);
                    _logger.LogInformation("Reset {Count} interrupted jobs back to Queued.", activeJobs.Count);
                }
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                DownloadJob? jobToProcess = null;

                using (var scope = _serviceProvider.CreateScope())
                {
                    var db = scope.ServiceProvider.GetRequiredService<DownloadDbContext>();
                    jobToProcess = db.Downloads
                        .Where(x => x.Status == "Queued")
                        .OrderBy(x => x.CreatedAt)
                        .FirstOrDefault();

                    if (jobToProcess != null)
                    {
                        jobToProcess.Status = "Downloading";
                        jobToProcess.UpdatedAt = DateTime.UtcNow;
                        await db.SaveChangesAsync(stoppingToken);
                    }
                }

                if (jobToProcess != null)
                {
                    _logger.LogInformation("Starting download job: {Title} (ID: {JobId})", jobToProcess.Title, jobToProcess.Id);
                    
                    var jobCts = new CancellationTokenSource();
                    _activeTokens[jobToProcess.Id] = jobCts;

                    using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, jobCts.Token);

                    var startTime = DateTime.UtcNow;
                    var runner = new SubprocessRunner();

                    double lastProgress = 0;
                    var lastUpdate = DateTime.UtcNow;

                    Action<double, string, string> progressCallback = (prog, speed, size) =>
                    {
                        var now = DateTime.UtcNow;
                        bool shouldUpdate = false;

                        if (prog >= 0 && Math.Abs(prog - lastProgress) >= 1.0)
                        {
                            lastProgress = prog;
                            shouldUpdate = true;
                        }
                        if (now - lastUpdate >= TimeSpan.FromSeconds(1.5))
                        {
                            shouldUpdate = true;
                        }
                        if (prog >= 100.0)
                        {
                            shouldUpdate = true;
                        }

                        if (shouldUpdate)
                        {
                            lastUpdate = now;
                            using var updateScope = _serviceProvider.CreateScope();
                            var dbContext = updateScope.ServiceProvider.GetRequiredService<DownloadDbContext>();
                            var dbJob = dbContext.Downloads.Find(jobToProcess.Id);
                            if (dbJob != null && dbJob.Status == "Downloading")
                            {
                                if (prog >= 0) dbJob.Progress = prog;
                                if (!string.IsNullOrEmpty(speed)) dbJob.Speed = speed;
                                if (!string.IsNullOrEmpty(size)) dbJob.Size = size;
                                dbJob.UpdatedAt = DateTime.UtcNow;
                                dbContext.SaveChanges();
                            }
                        }
                    };

                    bool success = false;
                    try
                    {
                        success = await runner.RunDownloadAsync(jobToProcess, progressCallback, combinedCts.Token);
                    }
                    catch (Exception ex)
                    {
                        jobToProcess.ErrorMessage = $"Error during execution: {ex.Message}";
                        _logger.LogError(ex, "Error executing download job {JobId}.", jobToProcess.Id);
                    }

                    // Process results
                    using (var scope = _serviceProvider.CreateScope())
                    {
                        var db = scope.ServiceProvider.GetRequiredService<DownloadDbContext>();
                        var dbJob = db.Downloads.Find(jobToProcess.Id);
                        if (dbJob != null)
                        {
                            if (success && !combinedCts.IsCancellationRequested)
                            {
                                try
                                {
                                    string finalPath = HandleFileCompletion(dbJob, startTime);
                                    dbJob.Status = "Completed";
                                    dbJob.Progress = 100.0;
                                    dbJob.Speed = "0 B/s";
                                    dbJob.DownloadedTo = finalPath;
                                    _logger.LogInformation("Job {JobId} completed successfully. Saved to {Path}", dbJob.Id, finalPath);
                                }
                                catch (Exception ex)
                                {
                                    dbJob.Status = "Failed";
                                    dbJob.ErrorMessage = $"File post-processing error: {ex.Message}";
                                    _logger.LogError(ex, "Failed file post-processing for job {JobId}.", dbJob.Id);
                                }
                            }
                            else
                            {
                                dbJob.Status = jobCts.IsCancellationRequested ? "Deleted" : "Failed";
                                dbJob.ErrorMessage = dbJob.ErrorMessage ?? jobToProcess.ErrorMessage ?? "Download cancelled or failed.";
                                dbJob.Speed = "0 B/s";
                                CleanPartialFiles(dbJob);
                                _logger.LogWarning("Job {JobId} failed or cancelled. Status: {Status}. Error: {Error}", dbJob.Id, dbJob.Status, dbJob.ErrorMessage);
                            }

                            dbJob.UpdatedAt = DateTime.UtcNow;
                            await db.SaveChangesAsync(stoppingToken);
                        }
                    }

                    _activeTokens.TryRemove(jobToProcess.Id, out _);
                    jobCts.Dispose();
                }
                else
                {
                    // No work, sleep for a bit
                    await Task.Delay(1000, stoppingToken);
                }
            }
        }

        private string HandleFileCompletion(DownloadJob job, DateTime startTime)
        {
            AppConfig.EnsureDirectoriesExist();
            string finalDest = Path.Combine(AppConfig.CompletedFolder, job.Filename);

            bool isAniCli = job.StreamUrl.StartsWith("ani-cli:", StringComparison.OrdinalIgnoreCase) || 
                            string.IsNullOrEmpty(job.StreamUrl) || 
                            job.StreamUrl.Equals("ani-cli", StringComparison.OrdinalIgnoreCase);

            if (isAniCli)
            {
                // ani-cli creates files inside AppConfig.IncompleteFolder, potentially inside a subfolder
                // Scan for the most recently modified file in IncompleteFolder that fits video types
                var searchDir = new DirectoryInfo(AppConfig.IncompleteFolder);
                var downloadedFile = searchDir.GetFiles("*.*", SearchOption.AllDirectories)
                    .Where(f => f.LastWriteTimeUtc >= startTime.AddSeconds(-30)) // Give buffer for clock diffs
                    .Where(f => !f.Name.EndsWith(".temp") && !f.Name.EndsWith(".part"))
                    .OrderByDescending(f => f.LastWriteTimeUtc)
                    .FirstOrDefault();

                if (downloadedFile == null)
                {
                    throw new FileNotFoundException("Could not locate the file downloaded by ani-cli.");
                }

                _logger.LogInformation("Located downloaded file from ani-cli: {FilePath}", downloadedFile.FullName);
                
                if (File.Exists(finalDest))
                {
                    File.Delete(finalDest);
                }
                
                File.Move(downloadedFile.FullName, finalDest);

                // Clean up any parent directory if ani-cli created a subfolder and it is empty
                if (downloadedFile.DirectoryName != null && 
                    !downloadedFile.DirectoryName.Equals(AppConfig.IncompleteFolder, StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        if (!Directory.EnumerateFileSystemEntries(downloadedFile.DirectoryName).Any())
                        {
                            Directory.Delete(downloadedFile.DirectoryName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to clean empty ani-cli subfolder {Dir}.", downloadedFile.DirectoryName);
                    }
                }
            }
            else
            {
                // yt-dlp downloaded to AppConfig.IncompleteFolder / job.Filename.temp
                string tempFile = Path.Combine(AppConfig.IncompleteFolder, $"{job.Filename}.temp");
                if (!File.Exists(tempFile))
                {
                    // Check if yt-dlp saved it without the temp extension or with some other extension, fallback search
                    tempFile = Directory.GetFiles(AppConfig.IncompleteFolder, $"{job.Filename}*")
                        .FirstOrDefault() ?? string.Empty;

                    if (string.IsNullOrEmpty(tempFile) || !File.Exists(tempFile))
                    {
                        throw new FileNotFoundException($"Could not find yt-dlp temp file: {job.Filename}.temp");
                    }
                }

                if (File.Exists(finalDest))
                {
                    File.Delete(finalDest);
                }

                File.Move(tempFile, finalDest);
            }

            return finalDest;
        }

        private void CleanPartialFiles(DownloadJob job)
        {
            try
            {
                // Remove yt-dlp temp file
                string tempFile = Path.Combine(AppConfig.IncompleteFolder, $"{job.Filename}.temp");
                if (File.Exists(tempFile))
                {
                    File.Delete(tempFile);
                }

                // Check for any part files yt-dlp or aria2c might have left
                var partFiles = Directory.GetFiles(AppConfig.IncompleteFolder, $"{job.Filename}*");
                foreach (var file in partFiles)
                {
                    if (File.Exists(file))
                    {
                        File.Delete(file);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to clean partial download files for job {JobId}.", job.Id);
            }
        }
    }
}
