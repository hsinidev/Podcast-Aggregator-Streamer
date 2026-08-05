using System;
using System.Text.Json.Serialization;

namespace PodcastAggregatorStreamer.Models
{
    public class EpisodeChapter
    {
        [JsonPropertyName("title")]
        public string Title { get; set; } = string.Empty;

        [JsonPropertyName("startTimeSeconds")]
        public double StartTimeSeconds { get; set; }

        [JsonPropertyName("endTimeSeconds")]
        public double EndTimeSeconds { get; set; }

        [JsonPropertyName("imageUrl")]
        public string? ImageUrl { get; set; }

        [JsonPropertyName("url")]
        public string? Url { get; set; }

        public string FormattedStartTime => TimeSpan.FromSeconds(StartTimeSeconds).ToString(@"hh\:mm\:ss");
        public string FormattedDuration => TimeSpan.FromSeconds(Math.Max(0, EndTimeSeconds - StartTimeSeconds)).ToString(@"mm\:ss");
    }
}
