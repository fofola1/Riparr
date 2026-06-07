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

// Ensure Directories are Created on App Start
AppConfig.EnsureDirectoriesExist();

// Global API Key validation helper
bool IsApiKeyValid(HttpContext context)
{
    var expectedKey = AppConfig.ApiKey;
    if (string.IsNullOrEmpty(expectedKey)) return true; // No API key configured, allow all

    var requestKey = context.Request.Query["apikey"].FirstOrDefault() ?? 
                     context.Request.Headers["SABnzbd-Key"].FirstOrDefault();

    return string.Equals(expectedKey, requestKey, StringComparison.Ordinal);
}

// Map the SABnzbd endpoint
async Task HandleSabnzbdRequest(HttpContext context, DownloadDbContext db, DownloadManager downloadManager, ILogger<Program> logger)
{
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

    logger.LogInformation("API request received: mode={Mode}, name={Name}, value={Value}", mode, name, value);

    switch (mode.ToLowerInvariant())
    {
        case "version":
            await context.Response.WriteAsJsonAsync(new SabnzbdVersionResponse());
            break;

        case "addurl":
            await HandleAddUrlAsync(context, name, db, logger);
            break;

        case "queue":
            if (name.Equals("delete", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
            {
                await HandleDeleteJobAsync(context, value, db, downloadManager, logger);
            }
            else
            {
                await HandleGetQueueAsync(context, db);
            }
            break;

        case "history":
            if (name.Equals("delete", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(value))
            {
                await HandleDeleteJobAsync(context, value, db, downloadManager, logger);
            }
            else
            {
                await HandleGetHistoryAsync(context, db);
            }
            break;

        case "status":
        case "qstatus":
            await HandleGetQueueAsync(context, db);
            break;

        default:
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { status = false, error = $"Unsupported mode: {mode}" });
            break;
    }
}

async Task HandleAddUrlAsync(HttpContext context, string nameUrl, DownloadDbContext db, ILogger<Program> logger)
{
    if (string.IsNullOrEmpty(nameUrl))
    {
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { status = false, error = "Missing 'name' (URL) parameter" });
        return;
    }

    try
    {
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
            context.Response.StatusCode = 400;
            await context.Response.WriteAsJsonAsync(new { status = false, error = "Invalid Payload JSON values" });
            return;
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
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow
        };

        db.Downloads.Add(job);
        await db.SaveChangesAsync();

        logger.LogInformation("Job added to queue: {Title} -> {Filename} (ID: {JobId})", job.Title, job.Filename, nzoId);

        await context.Response.WriteAsJsonAsync(new SabnzbdAddUrlResponse
        {
            Status = true,
            NzoIds = new() { nzoId }
        });
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Failed to decode/add job payload.");
        context.Response.StatusCode = 400;
        await context.Response.WriteAsJsonAsync(new { status = false, error = $"Failed to process payload: {ex.Message}" });
    }
}

async Task HandleGetQueueAsync(HttpContext context, DownloadDbContext db)
{
    var activeJobs = await db.Downloads
        .Where(x => x.Status == "Queued" || x.Status == "Downloading")
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
            Percentage = job.Progress.ToString("F1"),
            Size = $"{totalMb} MB",
            SizeLeft = $"{mbLeft:F1} MB",
            Mb = totalMb.ToString("F2"),
            MbLeft = mbLeft.ToString("F2"),
            Speed = job.Speed,
            TimeLeft = "0:00:00" // Mocked time remaining
        };
    }).ToList();

    double totalActiveMbLeft = slots.Sum(s => double.TryParse(s.MbLeft, out var v) ? v : 0);
    double totalActiveMb = slots.Sum(s => double.TryParse(s.Mb, out var v) ? v : 0);

    var response = new SabnzbdQueueResponse
    {
        Queue = new SabnzbdQueue
        {
            Status = activeJobs.Any(x => x.Status == "Downloading") ? "Downloading" : "Idle",
            Speed = activeJobs.FirstOrDefault(x => x.Status == "Downloading")?.Speed ?? "0 B/s",
            Size = $"{totalActiveMb:F1} MB",
            SizeLeft = $"{totalActiveMbLeft:F1} MB",
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
        Status = job.Status == "Completed" ? "Completed" : "Failed",
        Size = job.Size != "0 B" && !string.IsNullOrEmpty(job.Size) ? job.Size : "400 MB",
        Category = "tv",
        DownloadedTo = job.DownloadedTo ?? AppConfig.CompletedFolder,
        FailMessage = job.ErrorMessage ?? string.Empty
    }).ToList();

    await context.Response.WriteAsJsonAsync(new SabnzbdHistoryResponse
    {
        History = new SabnzbdHistory
        {
            Slots = slots
        }
    });
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
