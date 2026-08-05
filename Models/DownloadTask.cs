using System;

namespace PodcastAggregatorStreamer.Models
{
    public class DownloadTask
    {
        public string EpisodeId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string PodcastName { get; set; } = string.Empty;
        public string DownloadUrl { get; set; } = string.Empty;
        public string TargetPath { get; set; } = string.Empty;

        public long BytesDownloaded { get; set; }
        public long TotalBytes { get; set; }
        public double ProgressPercentage => TotalBytes > 0 ? (double)BytesDownloaded / TotalBytes * 100.0 : 0.0;

        public double SpeedKbps { get; set; }
        public EpisodeDownloadState State { get; set; } = EpisodeDownloadState.Queued;
        public string ErrorMessage { get; set; } = string.Empty;
    }

    public class PlaybackTelemetry
    {
        public string EpisodeId { get; set; } = string.Empty;
        public string EpisodeTitle { get; set; } = string.Empty;
        public string PodcastTitle { get; set; } = string.Empty;
        public double CurrentTimeSeconds { get; set; }
        public double TotalTimeSeconds { get; set; }
        public double BufferProgressPercentage { get; set; }
        public float Volume { get; set; } = 1.0f;
        public float PlaybackSpeed { get; set; } = 1.0f;
        public bool IsPlaying { get; set; }
        public bool IsBuffering { get; set; }
        public string ActiveChapterTitle { get; set; } = string.Empty;
    }
}
