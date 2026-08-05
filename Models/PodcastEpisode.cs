using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Text.Json;

namespace PodcastAggregatorStreamer.Models
{
    public enum EpisodeDownloadState
    {
        NotDownloaded,
        Queued,
        Downloading,
        Downloaded,
        Failed
    }

    public class PodcastEpisode
    {
        [Key]
        public string Id { get; set; } = System.Guid.NewGuid().ToString();

        public string PodcastFeedId { get; set; } = string.Empty;
        public PodcastFeed? PodcastFeed { get; set; }

        public string EpisodeGuid { get; set; } = string.Empty;
        public string Title { get; set; } = "Untitled Episode";
        public string Description { get; set; } = string.Empty;
        public string ShowNotes { get; set; } = string.Empty;

        public DateTime PubDate { get; set; } = DateTime.UtcNow;
        public double DurationSeconds { get; set; }

        public string AudioUrl { get; set; } = string.Empty;
        public string LocalFilePath { get; set; } = string.Empty;

        public EpisodeDownloadState DownloadState { get; set; } = EpisodeDownloadState.NotDownloaded;
        public double DownloadProgress { get; set; } // 0.0 to 100.0

        public double PlaybackPositionSeconds { get; set; }
        public bool IsListened { get; set; }
        public string ArtworkUrl { get; set; } = string.Empty;

        public string ChaptersJson { get; set; } = "[]";

        [NotMapped]
        public List<EpisodeChapter> Chapters
        {
            get
            {
                if (string.IsNullOrWhiteSpace(ChaptersJson)) return new List<EpisodeChapter>();
                try
                {
                    return JsonSerializer.Deserialize<List<EpisodeChapter>>(ChaptersJson) ?? new List<EpisodeChapter>();
                }
                catch
                {
                    return new List<EpisodeChapter>();
                }
            }
            set
            {
                ChaptersJson = JsonSerializer.Serialize(value ?? new List<EpisodeChapter>());
            }
        }

        public string FormattedPubDate => PubDate.ToString("yyyy-MM-dd HH:mm");
        public string FormattedDuration => TimeSpan.FromSeconds(DurationSeconds).ToString(@"hh\:mm\:ss");
        public string FormattedPosition => TimeSpan.FromSeconds(PlaybackPositionSeconds).ToString(@"hh\:mm\:ss");
    }
}
