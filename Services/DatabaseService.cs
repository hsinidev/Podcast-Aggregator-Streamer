using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using PodcastAggregatorStreamer.Models;

namespace PodcastAggregatorStreamer.Services
{
    public class PodcastDbContext : DbContext
    {
        public DbSet<PodcastFeed> Feeds { get; set; } = null!;
        public DbSet<PodcastEpisode> Episodes { get; set; } = null!;

        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
        {
            string dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "podcasts.db");
            optionsBuilder.UseSqlite($"Data Source={dbPath}");
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            modelBuilder.Entity<PodcastFeed>()
                .HasMany(f => f.Episodes)
                .WithOne(e => e.PodcastFeed)
                .HasForeignKey(e => e.PodcastFeedId)
                .OnDelete(DeleteBehavior.Cascade);
        }
    }

    public class DatabaseService
    {
        public DatabaseService()
        {
            using var db = new PodcastDbContext();
            db.Database.EnsureCreated();
        }

        public async Task<List<PodcastFeed>> GetAllFeedsAsync()
        {
            using var db = new PodcastDbContext();
            return await db.Feeds
                .Include(f => f.Episodes)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task SaveFeedAsync(PodcastFeed feed)
        {
            using var db = new PodcastDbContext();
            var existing = await db.Feeds.FirstOrDefaultAsync(f => f.FeedUrl == feed.FeedUrl || f.Id == feed.Id);
            if (existing == null)
            {
                db.Feeds.Add(feed);
            }
            else
            {
                existing.Title = feed.Title;
                existing.WebsiteUrl = feed.WebsiteUrl;
                existing.Description = feed.Description;
                existing.ArtworkUrl = feed.ArtworkUrl;
                existing.Author = feed.Author;
                existing.Category = feed.Category;
                existing.LastSyncedAt = DateTime.UtcNow;
                existing.UnreadCount = feed.Episodes.Count(e => !e.IsListened);
            }
            await db.SaveChangesAsync();
        }

        public async Task SaveEpisodesAsync(string feedId, IEnumerable<PodcastEpisode> newEpisodes)
        {
            using var db = new PodcastDbContext();
            var existingEpisodes = await db.Episodes.Where(e => e.PodcastFeedId == feedId).ToListAsync();

            foreach (var ep in newEpisodes)
            {
                var match = existingEpisodes.FirstOrDefault(e => (!string.IsNullOrEmpty(e.EpisodeGuid) && e.EpisodeGuid == ep.EpisodeGuid) || e.AudioUrl == ep.AudioUrl);
                if (match == null)
                {
                    ep.PodcastFeedId = feedId;
                    db.Episodes.Add(ep);
                }
                else
                {
                    match.Title = ep.Title;
                    match.Description = ep.Description;
                    match.ShowNotes = ep.ShowNotes;
                    match.DurationSeconds = ep.DurationSeconds > 0 ? ep.DurationSeconds : match.DurationSeconds;
                    if (string.IsNullOrEmpty(match.ArtworkUrl)) match.ArtworkUrl = ep.ArtworkUrl;
                }
            }

            var feed = await db.Feeds.FindAsync(feedId);
            if (feed != null)
            {
                feed.LastSyncedAt = DateTime.UtcNow;
                feed.UnreadCount = await db.Episodes.CountAsync(e => e.PodcastFeedId == feedId && !e.IsListened);
            }

            await db.SaveChangesAsync();
        }

        public async Task DeleteFeedAsync(string feedId)
        {
            using var db = new PodcastDbContext();
            var feed = await db.Feeds.Include(f => f.Episodes).FirstOrDefaultAsync(f => f.Id == feedId);
            if (feed != null)
            {
                db.Feeds.Remove(feed);
                await db.SaveChangesAsync();
            }
        }

        public async Task UpdateEpisodePlaybackPositionAsync(string episodeId, double positionSeconds, bool isListened)
        {
            using var db = new PodcastDbContext();
            var episode = await db.Episodes.FindAsync(episodeId);
            if (episode != null)
            {
                episode.PlaybackPositionSeconds = positionSeconds;
                if (isListened) episode.IsListened = true;
                await db.SaveChangesAsync();

                // Update unread count for feed
                var feed = await db.Feeds.FindAsync(episode.PodcastFeedId);
                if (feed != null)
                {
                    feed.UnreadCount = await db.Episodes.CountAsync(e => e.PodcastFeedId == episode.PodcastFeedId && !e.IsListened);
                    await db.SaveChangesAsync();
                }
            }
        }

        public async Task UpdateEpisodeDownloadStatusAsync(string episodeId, EpisodeDownloadState state, string localPath, double progress = 100.0)
        {
            using var db = new PodcastDbContext();
            var episode = await db.Episodes.FindAsync(episodeId);
            if (episode != null)
            {
                episode.DownloadState = state;
                episode.LocalFilePath = localPath;
                episode.DownloadProgress = progress;
                await db.SaveChangesAsync();
            }
        }

        public async Task MarkEpisodeListenedAsync(string episodeId, bool isListened)
        {
            using var db = new PodcastDbContext();
            var episode = await db.Episodes.FindAsync(episodeId);
            if (episode != null)
            {
                episode.IsListened = isListened;
                await db.SaveChangesAsync();

                var feed = await db.Feeds.FindAsync(episode.PodcastFeedId);
                if (feed != null)
                {
                    feed.UnreadCount = await db.Episodes.CountAsync(e => e.PodcastFeedId == episode.PodcastFeedId && !e.IsListened);
                    await db.SaveChangesAsync();
                }
            }
        }

        public async Task<List<PodcastEpisode>> GetAllDownloadedEpisodesAsync()
        {
            using var db = new PodcastDbContext();
            return await db.Episodes
                .Include(e => e.PodcastFeed)
                .Where(e => e.DownloadState == EpisodeDownloadState.Downloaded && !string.IsNullOrEmpty(e.LocalFilePath))
                .OrderByDescending(e => e.PubDate)
                .AsNoTracking()
                .ToListAsync();
        }

        public async Task UpdateChaptersJsonAsync(string episodeId, string chaptersJson)
        {
            using var db = new PodcastDbContext();
            var episode = await db.Episodes.FindAsync(episodeId);
            if (episode != null)
            {
                episode.ChaptersJson = chaptersJson;
                await db.SaveChangesAsync();
            }
        }
    }
}
