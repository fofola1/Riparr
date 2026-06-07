using System.Text.Json.Serialization;

namespace Riparr.Models
{
    public class PayloadModel
    {
        [JsonPropertyName("site")]
        public string? Site { get; set; }

        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("title")]
        public string? Title { get; set; }

        [JsonPropertyName("season")]
        public int? Season { get; set; }

        [JsonPropertyName("ep")]
        public int? Episode { get; set; }

        [JsonPropertyName("stream_url")]
        public string? StreamUrl { get; set; }

        [JsonPropertyName("resolution")]
        public string? Resolution { get; set; }

        [JsonPropertyName("source")]
        public string? Source { get; set; }
    }
}
