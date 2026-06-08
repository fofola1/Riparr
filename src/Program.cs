using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.EntityFrameworkCore;
using Riparr.Config;
using Riparr.Data;
using Riparr.Models;
using Riparr.Services;

var builder = WebApplication.CreateBuilder(args);

// Configure Logging
builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.SetMinimumLevel(LogLevel.Information);

// Configure Kestrel Port
builder.WebHost.ConfigureKestrel(options =>
{
    int port = int.TryParse(AppConfig.Port, out var p) ? p : 8080;
    options.ListenAnyIP(port);
});

// Register Database and Background Services
builder.Services.AddDbContext<DownloadDbContext>(options =>
    options.UseSqlite($"Data Source={AppConfig.DbPath}"));

builder.Services.AddSingleton<DownloadManager>();
builder.Services.AddHostedService<DownloadManager>(sp => sp.GetRequiredService<DownloadManager>());

var app = builder.Build();

// Ensure Directories are Created on App Start (especially the DB folder)
AppConfig.EnsureDirectoriesExist();

// Ensure database and schema are created before the app starts accepting HTTP requests
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<DownloadDbContext>();
    db.Database.EnsureCreated();
}

// Enable Static Files Serving for Web UI
app.UseDefaultFiles();
app.UseStaticFiles();

// Global API Key validation helper
bool IsApiKeyValid(HttpContext context)
{
    var expectedKey = AppConfig.ApiKey;
    if (string.IsNullOrEmpty(expectedKey)) return true; // No API key configured, allow all

    // Bypass API key validation if the request contains the 'payload' query parameter,
    // as the payload itself acts as a single-use authenticated token.
    if (context.Request.Query.ContainsKey("payload")) return true;

    var requestKey = context.Request.Query["apikey"].FirstOrDefault() ?? 
                     context.Request.Headers["SABnzbd-Key"].FirstOrDefault();

    return string.Equals(expectedKey, requestKey, StringComparison.Ordinal);
}

// Helper to register a download job internally from a Base64-encoded payload
async Task<DownloadJob?> RegisterDownloadJobInternalAsync(string payloadBase64, string cat, DownloadDbContext db, ILogger logger)
{
    try
    {
        // Clean up Base64 formatting (handle URL safe base64)
        payloadBase64 = payloadBase64.Replace('-', '+').Replace('_', '/');
        int mod = payloadBase64.Length % 4;
        if (mod > 0)
        {
            payloadBase64 += new string('=', 4 - mod);
        }

        byte[] payloadBytes = Convert.FromBase64String(payloadBase64);
        string payloadJson = Encoding.UTF8.GetString(payloadBytes);

        var payload = JsonSerializer.Deserialize<PayloadModel>(payloadJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (payload == null || (string.IsNullOrEmpty(payload.Title) && string.IsNullOrEmpty(payload.StreamUrl)))
        {
            return null;
        }

        // Formulate target filename
        string episodeStr = payload.Episode?.ToString() ?? "1";
        if (int.TryParse(episodeStr, out var epNum))
        {
            episodeStr = epNum.ToString("D2");
        }

        string filename = string.Empty;
        if (payload.Season.HasValue)
        {
            filename = $"{payload.Title} - S{payload.Season.Value:D2}E{episodeStr}";
        }
        else
        {
            filename = $"{payload.Title} - E{episodeStr}";
        }

        if (!string.IsNullOrEmpty(payload.Resolution))
        {
            filename += $" - {payload.Resolution}";
        }
        if (!string.IsNullOrEmpty(payload.Source))
        {
            filename += $" - {payload.Source}";
        }
        filename += ".mp4";

        // Sanitize filename
        foreach (char c in Path.GetInvalidFileNameChars())
        {
            filename = filename.Replace(c, '_');
        }

        string nzoId = $"SABnzbd_nzo_{Guid.NewGuid().ToString("n")}";

        var job = new DownloadJob
        {
            Id = nzoId,
            Title = payload.Title ?? "Unknown Anime",
            Season = payload.Season,
            Episode = payload.Episode?.ToString() ?? "1",
            StreamUrl = payload.StreamUrl ?? "ani-cli",
            Filename = filename,
            Status = "Queued",
            Category = string.IsNullOrEmpty(cat) ? "tv" : cat,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Downloads.Add(job);
        await db.SaveChangesAsync();

        logger.LogInformation("Job added to queue: {Title} -> {Filename} (ID: {JobId})", job.Title, job.Filename, nzoId);
        return job;
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to register download job from payload.");
        return null;
    }
}

// Map the SABnzbd endpoint
async Task HandleSabnzbdRequest(HttpContext context, DownloadDbContext db, DownloadManager downloadManager, ILogger<Program> logger)
{
    var requestKey = context.Request.Query["apikey"].FirstOrDefault() ?? 
                     context.Request.Headers["SABnzbd-Key"].FirstOrDefault() ?? "";
    logger.LogInformation("Incoming SABnzbd API request: mode={Mode}, apikey={ApiKey}", 
        context.Request.Query["mode"].FirstOrDefault(), requestKey);

    if (!IsApiKeyValid(context))
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { status = false, error = "API Key Incorrect" });
        return;
    }

    var query = context.Request.Query;
    string mode = query["mode"].FirstOrDefault() ?? string.Empty;
    string name = query["name"].FirstOrDefault() ?? string.Empty;
    string value = query["value"].FirstOrDefault() ?? string.Empty;
    string cat = query["cat"].FirstOrDefault() ?? "tv";

    logger.LogInformation("API request received: mode={Mode}, name={Name}, value={Value}, cat={Cat}", mode, name, value, cat);

    // Direct NZB request check: if mode is empty and payload is present
    if (string.IsNullOrEmpty(mode) && query.TryGetValue("payload", out var payloadVal))
    {
        string payloadBase64 = payloadVal.ToString();
        var job = await RegisterDownloadJobInternalAsync(payloadBase64, cat, db, logger);
        if (job == null)
        {
            context.Response.StatusCode = 400;
            await context.Response.WriteAsync("Error: Failed to process payload or invalid payload JSON values");
            return;
        }

        long epochSeconds = new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds();
        string nzbXml = $@"<?xml version=""1.0"" encoding=""utf-8"" ?>
<nzb xmlns=""http://www.newzbin.com/DTD/2003/nzb"">
  <file subject=""{System.Security.SecurityElement.Escape(job.Filename)}"" poster=""Otakarr"" date=""{epochSeconds}"">
    <groups><group>alt.binaries.boneless</group></groups>
    <segments><segment bytes=""1000"" number=""1"">{job.Id}@otakarr</segment></segments>
  </file>
</nzb>";

        context.Response.ContentType = "application/x-nzb";
        context.Response.Headers["Content-Disposition"] = $"attachment; filename=\"{job.Id}.nzb\"";
        await context.Response.WriteAsync(nzbXml, Encoding.UTF8);
        return;
    }

    switch (mode.ToLowerInvariant())
    {
        case "version":
            await context.Response.WriteAsJsonAsync(new SabnzbdVersionResponse());
            break;

        case "get_config":
            await context.Response.WriteAsJsonAsync(new
            {
                config = new
                {
                    misc = new
                    {
                        complete_dir = AppConfig.CompletedFolder,
                        pre_check = false,
                        enable_tv_sorting = false,
                        enable_movie_sorting = false,
                        enable_date_sorting = false,
                        tv_categories = new string[0],
                        movie_categories = new string[0],
                        date_categories = new string[0],
                        history_retention = "",
                        history_retention_option = "all",
                        history_retention_number = 0
                    },
                    categories = new[]
                    {
                        new { name = "*", dir = "" },
                        new { name = "movies", dir = "movies" },
                        new { name = "tv", dir = "tv" },
                        new { name = "music", dir = "music" },
                        new { name = "anime", dir = "anime" },
                        new { name = "radarr", dir = "radarr" },
                        new { name = "sonarr", dir = "sonarr" }
                    },
                    sorters = new object[0]
                }
            });
            break;

        case "get_cats":
            await context.Response.WriteAsJsonAsync(new
            {
                categories = new[]
                {
                    new { name = "*", dir = "" },
                    new { name = "movies", dir = "movies" },
                    new { name = "tv", dir = "tv" },
                    new { name = "music", dir = "music" },
                    new { name = "anime", dir = "anime" },
                    new { name = "radarr", dir = "radarr" },
                    new { name = "sonarr", dir = "sonarr" }
                }
            });
            break;

        case "pause":
            downloadManager.PauseQueue();
            await context.Response.WriteAsJsonAsync(new { status = true });
            break;

        case "resume":
            downloadManager.ResumeQueue();
            await context.Response.WriteAsJsonAsync(new { status = true });
            break;

        case "addurl":
            await HandleAddUrlAsync(context, name, cat, db, logger);
            break;

        case "addfile":
            string? uploadedNzoId = null;
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync();
                var file = form.Files.FirstOrDefault();
                if (file != null)
                {
                    using var reader = new StreamReader(file.OpenReadStream());
                    var content = await reader.ReadToEndAsync();
                    // Extract the nzoId from the NZB file content
                    var match = System.Text.RegularExpressions.Regex.Match(content, @"SABnzbd_nzo_[0-9a-fA-F]+");
                    if (match.Success)
                    {
                        uploadedNzoId = match.Value;
                    }
                }
            }
            if (string.IsNullOrEmpty(uploadedNzoId))
            {
                uploadedNzoId = $"SABnzbd_nzo_{Guid.NewGuid().ToString("n")}";
            }
            await context.Response.WriteAsJsonAsync(new { status = true, nzo_ids = new[] { uploadedNzoId } });
            break;

        case "queue":
            if (name.Equals("delete", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
            {
                await HandleDeleteJobAsync(context, value, db, downloadManager, logger);
            }
            else if (name.Equals("pause", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
            {
                await HandlePauseJobAsync(context, value, db, downloadManager, logger);
            }
            else if (name.Equals("resume", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
            {
                await HandleResumeJobAsync(context, value, db, logger);
            }
            else if (name.Equals("purge", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePurgeQueueAsync(context, db, downloadManager, logger);
            }
            else if (name.Equals("speedlimit", StringComparison.OrdinalIgnoreCase))
            {
                await context.Response.WriteAsJsonAsync(new { status = true });
            }
            else
            {
                await HandleGetQueueAsync(context, db, downloadManager);
            }
            break;

        case "history":
            if (name.Equals("delete", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
            {
                await HandleDeleteJobAsync(context, value, db, downloadManager, logger);
            }
            else if (name.Equals("purge", StringComparison.OrdinalIgnoreCase))
            {
                await HandlePurgeHistoryAsync(context, db, logger);
            }
            else
            {
                await HandleGetHistoryAsync(context, db);
            }
            break;

        case "status":
        case "qstatus":
            await HandleGetQueueAsync(context, db, downloadManager);
            break;

        default:
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { status = false, error = $"Unsupported mode: {mode}" });
            break;
    }
}

async Task HandleAddUrlAsync(HttpContext context, string nameUrl, string cat, DownloadDbContext db, ILogger<Program> logger)
{
    if (string.IsNullOrEmpty(nameUrl))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { status = false, error = "Missing 'name' (URL) parameter" });
        return;
    }

    // Decode Base64 Payload from URL
    string payloadBase64 = string.Empty;
    try
    {
        if (nameUrl.Contains("payload="))
        {
            var parts = nameUrl.Split("payload=");
            payloadBase64 = parts[1].Split('&')[0];
        }
        else
        {
            // Fallback, try parsing as direct URL
            var uri = new Uri(nameUrl);
            var queryParams = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(uri.Query);
            if (queryParams.TryGetValue("payload", out var payloadVal))
            {
                payloadBase64 = payloadVal.ToString();
            }
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to parse nameUrl as URI: {Url}", nameUrl);
    }

    if (string.IsNullOrEmpty(payloadBase64))
    {
        // If payload couldn't be extracted, fallback: check if nameUrl itself is base64 or contains it
        payloadBase64 = nameUrl;
    }

    var job = await RegisterDownloadJobInternalAsync(payloadBase64, cat, db, logger);
    if (job == null)
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { status = false, error = "Failed to process payload or invalid payload JSON values" });
        return;
    }

    await context.Response.WriteAsJsonAsync(new SabnzbdAddUrlResponse
    {
        Status = true,
        NzoIds = new() { job.Id }
    });
}

async Task HandleGetQueueAsync(HttpContext context, DownloadDbContext db, DownloadManager downloadManager)
{
    var activeJobs = await db.Downloads
        .Where(x => x.Status == "Queued" || x.Status == "Downloading" || x.Status == "Paused")
        .OrderBy(x => x.CreatedAt)
        .ToListAsync();

    var slots = activeJobs.Select((job, index) =>
    {
        double totalMb = 400.0; // Standard mocked episode size in MB
        double mbLeft = totalMb * (1.0 - job.Progress / 100.0);
        if (mbLeft < 0) mbLeft = 0;

        return new SabnzbdQueueSlot
        {
            Status = job.Status,
            Index = index,
            NzoId = job.Id,
            Filename = job.Filename,
            Percentage = (int)Math.Round(job.Progress),
            Size = $"{totalMb} MB",
            SizeLeft = $"{mbLeft:F1} MB",
            Mb = totalMb.ToString("F2"),
            MbLeft = mbLeft.ToString("F2"),
            Speed = job.Speed,
            TimeLeft = "0:00:00", // Mocked time remaining
            Cat = job.Category
        };
    }).ToList();

    double totalActiveMbLeft = slots.Sum(s => double.TryParse(s.MbLeft, out var v) ? v : 0);
    double totalActiveMb = slots.Sum(s => double.TryParse(s.Mb, out var v) ? v : 0);

    var response = new SabnzbdQueueResponse
    {
        Queue = new SabnzbdQueue
        {
            Status = downloadManager.IsQueuePaused ? "Paused" : (activeJobs.Any(x => x.Status == "Downloading") ? "Downloading" : "Idle"),
            Speed = activeJobs.FirstOrDefault(x => x.Status == "Downloading")?.Speed ?? "0 B/s",
            Size = $"{totalActiveMb:F1} MB",
            SizeLeft = $"{totalActiveMbLeft:F1} MB",
            Paused = downloadManager.IsQueuePaused,
            Slots = slots
        }
    };

    await context.Response.WriteAsJsonAsync(response);
}

async Task HandleGetHistoryAsync(HttpContext context, DownloadDbContext db)
{
    var finishedJobs = await db.Downloads
        .Where(x => x.Status == "Completed" || x.Status == "Failed")
        .OrderByDescending(x => x.UpdatedAt)
        .ToListAsync();

    var slots = finishedJobs.Select(job => new SabnzbdHistorySlot
    {
        NzoId = job.Id,
        Name = job.Filename,
        Status = job.Status,
        Size = job.Size != "0 B" && !string.IsNullOrEmpty(job.Size) ? job.Size : (job.Status == "Failed" ? "0 B" : "400 MB"),
        Category = job.Category,
        DownloadedTo = job.DownloadedTo ?? AppConfig.CompletedFolder,
        FailMessage = job.Status == "Failed" ? (job.ErrorMessage ?? "Download failed.") : string.Empty
    }).ToList();

    await context.Response.WriteAsJsonAsync(new SabnzbdHistoryResponse
    {
        History = new SabnzbdHistory
        {
            Slots = slots
        }
    });
}

async Task HandlePauseJobAsync(HttpContext context, string jobId, DownloadDbContext db, DownloadManager downloadManager, ILogger<Program> logger)
{
    var job = await db.Downloads.FindAsync(jobId);
    if (job != null)
    {
        if (job.Status == "Downloading" || job.Status == "Queued")
        {
            job.Status = "Paused";
            job.Speed = "0 B/s";
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            downloadManager.CancelJob(jobId);
            logger.LogInformation("Job {JobId} was paused manually.", jobId);
        }
    }
    await context.Response.WriteAsJsonAsync(new { status = true });
}

async Task HandleResumeJobAsync(HttpContext context, string jobId, DownloadDbContext db, ILogger<Program> logger)
{
    var job = await db.Downloads.FindAsync(jobId);
    if (job != null)
    {
        if (job.Status == "Paused" || job.Status == "Failed")
        {
            job.Status = "Queued";
            job.Speed = "0 B/s";
            job.ErrorMessage = null;
            job.UpdatedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
            logger.LogInformation("Job {JobId} was resumed manually.", jobId);
        }
    }
    await context.Response.WriteAsJsonAsync(new { status = true });
}

async Task HandlePurgeQueueAsync(HttpContext context, DownloadDbContext db, DownloadManager downloadManager, ILogger<Program> logger)
{
    var activeJobs = await db.Downloads
        .Where(x => x.Status == "Queued" || x.Status == "Downloading" || x.Status == "Paused")
        .ToListAsync();

    foreach (var job in activeJobs)
    {
        downloadManager.CancelJob(job.Id);
        db.Downloads.Remove(job);
    }
    
    await db.SaveChangesAsync();
    logger.LogInformation("Purged all jobs from queue.");
    await context.Response.WriteAsJsonAsync(new { status = true });
}

async Task HandlePurgeHistoryAsync(HttpContext context, DownloadDbContext db, ILogger<Program> logger)
{
    var finishedJobs = await db.Downloads
        .Where(x => x.Status == "Completed" || x.Status == "Failed")
        .ToListAsync();

    db.Downloads.RemoveRange(finishedJobs);
    await db.SaveChangesAsync();
    logger.LogInformation("Purged all jobs from history.");
    await context.Response.WriteAsJsonAsync(new { status = true });
}

async Task HandleDeleteJobAsync(HttpContext context, string jobId, DownloadDbContext db, DownloadManager downloadManager, ILogger<Program> logger)
{
    // Try to cancel running subprocess if applicable
    downloadManager.CancelJob(jobId);

    var job = await db.Downloads.FindAsync(jobId);
    if (job != null)
    {
        db.Downloads.Remove(job);
        await db.SaveChangesAsync();
        logger.LogInformation("Job {JobId} deleted from database.", jobId);
    }
    else
    {
        logger.LogWarning("Delete requested for non-existent Job {JobId}.", jobId);
    }

    await context.Response.WriteAsJsonAsync(new { status = true });
}

// Categories configuration endpoint (GET & POST)
var handleCategories = async (HttpContext context, string? remainder, ILogger<Program> logger) =>
{
    var requestKey = context.Request.Query["apikey"].FirstOrDefault() ?? 
                     context.Request.Headers["SABnzbd-Key"].FirstOrDefault() ?? "";
    logger.LogInformation("Incoming categories config request: remainder={Remainder}, apikey={ApiKey}", remainder, requestKey);

    if (!string.IsNullOrEmpty(remainder) && remainder != "/")
    {
        context.Response.StatusCode = 404;
        return;
    }

    if (!IsApiKeyValid(context))
    {
        logger.LogWarning("Unauthorized access request to /config/categories");
        context.Response.StatusCode = 401;
        await context.Response.WriteAsJsonAsync(new { status = false, error = "API Key Incorrect" });
        return;
    }
    logger.LogInformation("Categories configuration requested");
    await context.Response.WriteAsJsonAsync(new { categories = new[] { "*", "movies", "tv", "music", "anime", "radarr", "sonarr" } });
};

app.MapGet("/config/categories/{**remainder}", handleCategories);
app.MapPost("/config/categories/{**remainder}", handleCategories);

// Route Mappings for SABnzbd emulation
app.MapGet("/api/sabnzbd", HandleSabnzbdRequest);
app.MapPost("/api/sabnzbd", HandleSabnzbdRequest);

app.MapGet("/api", HandleSabnzbdRequest);
app.MapPost("/api", HandleSabnzbdRequest);

app.MapGet("/sabnzbd/api", HandleSabnzbdRequest);
app.MapPost("/sabnzbd/api", HandleSabnzbdRequest);

// Health check endpoint
app.MapGet("/healthz", () => Results.Ok("Healthy"));

app.Run();
