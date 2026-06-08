using System;

namespace Riparr.Models
{
    public class DownloadJob
    {
        public string Id { get; set; } = string.Empty; // SABnzbd nzo_id format: SABnzbd_nzo_...
        public string Title { get; set; } = string.Empty;
        public int? Season { get; set; }
        public string Episode { get; set; } = string.Empty;
        public string StreamUrl { get; set; } = string.Empty;
        public string Filename { get; set; } = string.Empty;
        public string Status { get; set; } = "Queued"; // Queued, Downloading, Completed, Failed, Deleted
        public string Category { get; set; } = "tv";
        public double Progress { get; set; } = 0.0;
        public string Speed { get; set; } = "0 B/s";
        public string Size { get; set; } = "0 B";
        public string? DownloadedTo { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }
}
