using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using PodcastAggregatorStreamer.Models;

namespace PodcastAggregatorStreamer.Services
{
    public class DownloadManagerService
    {
        private static readonly HttpClient HttpClient = new HttpClient();
        private readonly DatabaseService _databaseService;
        private readonly ConcurrentDictionary<string, DownloadTask> _activeDownloads = new();
        private readonly SemaphoreSlim _downloadSemaphore = new(3); // Max 3 concurrent downloads

        private readonly string _downloadDirectory;
        private long _maxStorageLimitBytes = 5L * 1024L * 1024L * 1024L; // 5 GB default storage cap

        public event Action<DownloadTask>? DownloadProgressUpdated;
        public event Action<DownloadTask>? DownloadCompleted;
        public event Action<DownloadTask>? DownloadFailed;

        public ConcurrentDictionary<string, DownloadTask> ActiveDownloads => _activeDownloads;

        public DownloadManagerService(DatabaseService databaseService)
        {
            _databaseService = databaseService;
            _downloadDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Downloads");
            if (!Directory.Exists(_downloadDirectory))
            {
                Directory.CreateDirectory(_downloadDirectory);
            }
        }

        public void SetStorageLimitMb(long megabytes)
        {
            _maxStorageLimitBytes = Math.Max(500, megabytes) * 1024L * 1024L;
        }

        public async Task QueueDownloadAsync(PodcastEpisode episode, PodcastFeed? feed = null)
        {
            if (string.IsNullOrEmpty(episode.AudioUrl)) return;

            string sanitizeFileName(string name)
            {
                foreach (char c in Path.GetInvalidFileNameChars())
                {
                    name = name.Replace(c, '_');
                }
                return name;
            }

            string ext = Path.GetExtension(new Uri(episode.AudioUrl).AbsolutePath);
            if (string.IsNullOrEmpty(ext) || ext.Length > 5) ext = ".mp3";

            string fileName = $"{sanitizeFileName(episode.Title)}_{episode.Id.Substring(0, 8)}{ext}";
            string targetPath = Path.Combine(_downloadDirectory, fileName);

            var task = new DownloadTask
            {
                EpisodeId = episode.Id,
                Title = episode.Title,
                PodcastName = feed?.Title ?? episode.PodcastFeed?.Title ?? "Podcast",
                DownloadUrl = episode.AudioUrl,
                TargetPath = targetPath,
                State = EpisodeDownloadState.Queued
            };

            _activeDownloads[episode.Id] = task;
            await _databaseService.UpdateEpisodeDownloadStatusAsync(episode.Id, EpisodeDownloadState.Queued, targetPath, 0);

            _ = Task.Run(() => ProcessDownloadAsync(task, episode));
        }

        private async Task ProcessDownloadAsync(DownloadTask task, PodcastEpisode episode)
        {
            await _downloadSemaphore.WaitAsync();
            try
            {
                // Auto-prune old episodes if needed before starting new download
                await AutoPruneStorageIfNeededAsync();

                task.State = EpisodeDownloadState.Downloading;
                DownloadProgressUpdated?.Invoke(task);
                await _databaseService.UpdateEpisodeDownloadStatusAsync(episode.Id, EpisodeDownloadState.Downloading, task.TargetPath, 0);

                using var response = await HttpClient.GetAsync(task.DownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                task.TotalBytes = response.Content.Headers.ContentLength ?? 0;

                using var inputStream = await response.Content.ReadAsStreamAsync();
                using var outputStream = new FileStream(task.TargetPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);

                byte[] buffer = new byte[16384];
                int bytesRead;
                long totalRead = 0;
                var stopwatch = Stopwatch.StartNew();
                long lastBytesCount = 0;
                DateTime lastSpeedCheck = DateTime.UtcNow;

                while ((bytesRead = await inputStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    await outputStream.WriteAsync(buffer, 0, bytesRead);
                    totalRead += bytesRead;
                    task.BytesDownloaded = totalRead;

                    var now = DateTime.UtcNow;
                    var timeDiff = (now - lastSpeedCheck).TotalSeconds;
                    if (timeDiff >= 0.5)
                    {
                        double bytesDiff = totalRead - lastBytesCount;
                        task.SpeedKbps = (bytesDiff / 1024.0) / timeDiff;
                        lastBytesCount = totalRead;
                        lastSpeedCheck = now;

                        DownloadProgressUpdated?.Invoke(task);
                        await _databaseService.UpdateEpisodeDownloadStatusAsync(episode.Id, EpisodeDownloadState.Downloading, task.TargetPath, task.ProgressPercentage);
                    }
                }

                stopwatch.Stop();
                task.State = EpisodeDownloadState.Downloaded;
                DownloadCompleted?.Invoke(task);

                await _databaseService.UpdateEpisodeDownloadStatusAsync(episode.Id, EpisodeDownloadState.Downloaded, task.TargetPath, 100.0);
            }
            catch (Exception ex)
            {
                task.State = EpisodeDownloadState.Failed;
                task.ErrorMessage = ex.Message;
                DownloadFailed?.Invoke(task);
                await _databaseService.UpdateEpisodeDownloadStatusAsync(episode.Id, EpisodeDownloadState.Failed, string.Empty, 0);
            }
            finally
            {
                _downloadSemaphore.Release();
                _activeDownloads.TryRemove(episode.Id, out _);
            }
        }

        public async Task AutoPruneStorageIfNeededAsync()
        {
            try
            {
                var dirInfo = new DirectoryInfo(_downloadDirectory);
                if (!dirInfo.Exists) return;

                long totalBytesUsed = dirInfo.GetFiles().Sum(f => f.Length);
                if (totalBytesUsed <= _maxStorageLimitBytes) return;

                // Query listened episodes that have downloaded files
                var downloadedEpisodes = await _databaseService.GetAllDownloadedEpisodesAsync();
                var pruneCandidates = downloadedEpisodes
                    .Where(e => e.IsListened && File.Exists(e.LocalFilePath))
                    .OrderBy(e => e.PubDate)
                    .ToList();

                foreach (var ep in pruneCandidates)
                {
                    if (totalBytesUsed <= _maxStorageLimitBytes * 0.8) break; // Prune down to 80%

                    try
                    {
                        if (File.Exists(ep.LocalFilePath))
                        {
                            var fi = new FileInfo(ep.LocalFilePath);
                            long len = fi.Length;
                            fi.Delete();
                            totalBytesUsed -= len;
                        }
                        await _databaseService.UpdateEpisodeDownloadStatusAsync(ep.Id, EpisodeDownloadState.NotDownloaded, string.Empty, 0);
                    }
                    catch { }
                }
            }
            catch { }
        }

        public long GetTotalStorageBytesUsed()
        {
            var dirInfo = new DirectoryInfo(_downloadDirectory);
            if (!dirInfo.Exists) return 0;
            return dirInfo.GetFiles().Sum(f => f.Length);
        }
    }
}
