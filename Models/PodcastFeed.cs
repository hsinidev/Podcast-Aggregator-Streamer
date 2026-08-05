using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;

namespace PodcastAggregatorStreamer.Models
{
    public class PodcastFeed
    {
        [Key]
        public string Id { get; set; } = Guid.NewGuid().ToString();

        [Required]
        public string Title { get; set; } = "Untitled Podcast";

        public string FeedUrl { get; set; } = string.Empty;
        public string WebsiteUrl { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string ArtworkUrl { get; set; } = string.Empty;
        public string Author { get; set; } = string.Empty;
        public string Category { get; set; } = "General";

        public DateTime SubscribedAt { get; set; } = DateTime.UtcNow;
        public DateTime LastSyncedAt { get; set; } = DateTime.MinValue;

        public int UnreadCount { get; set; }
        public bool AutoDownload { get; set; } = false;

        public List<PodcastEpisode> Episodes { get; set; } = new();
    }
}
