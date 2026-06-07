using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace Riparr.Models
{
    public class SabnzbdAddUrlResponse
    {
        [JsonPropertyName("status")]
        public bool Status { get; set; }

        [JsonPropertyName("nzo_ids")]
        public List<string> NzoIds { get; set; } = new();
    }

    public class SabnzbdVersionResponse
    {
        [JsonPropertyName("version")]
        public string Version { get; set; } = "3.7.2";
    }

    public class SabnzbdQueueResponse
    {
        [JsonPropertyName("queue")]
        public SabnzbdQueue Queue { get; set; } = new();
    }

    public class SabnzbdQueue
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "Idle";

        [JsonPropertyName("speed")]
        public string Speed { get; set; } = "0";

        [JsonPropertyName("size")]
        public string Size { get; set; } = "0";

        [JsonPropertyName("sizeleft")]
        public string SizeLeft { get; set; } = "0";

        [JsonPropertyName("paused")]
        public bool Paused { get; set; } = false;

        [JsonPropertyName("slots")]
        public List<SabnzbdQueueSlot> Slots { get; set; } = new();
    }

    public class SabnzbdQueueSlot
    {
        [JsonPropertyName("status")]
        public string Status { get; set; } = "Queued"; // Downloading, Queued, Paused

        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("nzo_id")]
        public string NzoId { get; set; } = string.Empty;

        [JsonPropertyName("filename")]
        public string Filename { get; set; } = string.Empty;

        [JsonPropertyName("percentage")]
        public string Percentage { get; set; } = "0";

        [JsonPropertyName("size")]
        public string Size { get; set; } = "0 MB";

        [JsonPropertyName("sizeleft")]
        public string SizeLeft { get; set; } = "0 MB";

        [JsonPropertyName("timeleft")]
        public string TimeLeft { get; set; } = "0:00:00";

        [JsonPropertyName("mb")]
        public string Mb { get; set; } = "0.00";

        [JsonPropertyName("mbleft")]
        public string MbLeft { get; set; } = "0.00";

        [JsonPropertyName("speed")]
        public string Speed { get; set; } = "0 B/s";

        [JsonPropertyName("priority")]
        public string Priority { get; set; } = "Normal";

        [JsonPropertyName("cat")]
        public string Cat { get; set; } = "tv";
    }

    public class SabnzbdHistoryResponse
    {
        [JsonPropertyName("history")]
        public SabnzbdHistory History { get; set; } = new();
    }

    public class SabnzbdHistory
    {
        [JsonPropertyName("slots")]
        public List<SabnzbdHistorySlot> Slots { get; set; } = new();
    }

    public class SabnzbdHistorySlot
    {
        [JsonPropertyName("nzo_id")]
        public string NzoId { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("status")]
        public string Status { get; set; } = "Completed"; // Completed, Failed

        [JsonPropertyName("size")]
        public string Size { get; set; } = "0 MB";

        [JsonPropertyName("category")]
        public string Category { get; set; } = "tv";

        [JsonPropertyName("downloaded_to")]
        public string DownloadedTo { get; set; } = string.Empty;

        [JsonPropertyName("storage")]
        public string Storage => DownloadedTo;

        [JsonPropertyName("path")]
        public string Path => DownloadedTo;

        [JsonPropertyName("archive_depth")]
        public int ArchiveDepth { get; set; } = 0;

        [JsonPropertyName("fail_message")]
        public string FailMessage { get; set; } = string.Empty;
    }
}
