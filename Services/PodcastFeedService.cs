using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using CodeHollow.FeedReader;
using PodcastAggregatorStreamer.Models;

namespace PodcastAggregatorStreamer.Services
{
    public class PodcastFeedService
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        public PodcastFeedService()
        {
            if (!HttpClient.DefaultRequestHeaders.Contains("User-Agent"))
            {
                HttpClient.DefaultRequestHeaders.Add("User-Agent", "PodcastAggregatorStreamer/2.0 (Windows NT 10.0; Win64; x64)");
            }
        }

        public async Task<PodcastFeed> FetchFeedAsync(string feedUrl, CancellationToken cancellationToken = default)
        {
            var feed = new PodcastFeed
            {
                FeedUrl = feedUrl,
                SubscribedAt = DateTime.UtcNow,
                LastSyncedAt = DateTime.UtcNow
            };

            try
            {
                string xmlContent = await HttpClient.GetStringAsync(feedUrl, cancellationToken);
                XDocument doc = XDocument.Parse(xmlContent);
                XNamespace itunesNs = "http://www.itunes.com/dtds/podcast-1.0.dtd";

                var channel = doc.Root?.Element("channel");
                if (channel != null)
                {
                    feed.Title = channel.Element("title")?.Value?.Trim() ?? "Untitled Podcast";
                    feed.WebsiteUrl = channel.Element("link")?.Value?.Trim() ?? string.Empty;
                    feed.Description = channel.Element("description")?.Value?.Trim() 
                                      ?? channel.Element(itunesNs + "summary")?.Value?.Trim() 
                                      ?? string.Empty;
                    feed.Author = channel.Element(itunesNs + "author")?.Value?.Trim() 
                                  ?? channel.Element("managingEditor")?.Value?.Trim() 
                                  ?? string.Empty;

                    var categoryElem = channel.Element(itunesNs + "category");
                    feed.Category = categoryElem?.Attribute("text")?.Value ?? categoryElem?.Value ?? "General";

                    // Artwork
                    var imageElem = channel.Element("image");
                    string? imageLink = imageElem?.Element("url")?.Value;
                    string? itunesImageLink = channel.Element(itunesNs + "image")?.Attribute("href")?.Value;
                    feed.ArtworkUrl = !string.IsNullOrEmpty(itunesImageLink) ? itunesImageLink : (imageLink ?? string.Empty);

                    // Episodes
                    var items = channel.Elements("item");
                    foreach (var item in items)
                    {
                        var episode = ParseEpisodeItem(item, feed.Id, feed.ArtworkUrl, itunesNs);
                        if (episode != null)
                        {
                            feed.Episodes.Add(episode);
                        }
                    }
                }
                else
                {
                    // Fallback to CodeHollow FeedReader
                    var parsedFeed = await FeedReader.ReadAsync(feedUrl, cancellationToken);
                    feed.Title = parsedFeed.Title ?? "Untitled Podcast";
                    feed.WebsiteUrl = parsedFeed.Link ?? string.Empty;
                    feed.Description = parsedFeed.Description ?? string.Empty;
                    feed.ArtworkUrl = parsedFeed.ImageUrl ?? string.Empty;

                    foreach (var item in parsedFeed.Items)
                    {
                        var episode = new PodcastEpisode
                        {
                            PodcastFeedId = feed.Id,
                            EpisodeGuid = item.Id ?? item.Link ?? System.Guid.NewGuid().ToString(),
                            Title = item.Title ?? "Untitled Episode",
                            Description = item.Description ?? string.Empty,
                            ShowNotes = item.Content ?? item.Description ?? string.Empty,
                            PubDate = item.PublishingDate ?? DateTime.UtcNow,
                            AudioUrl = item.SpecificItem?.Element?.Element("enclosure")?.Attribute("url")?.Value ?? item.Link ?? string.Empty,
                            ArtworkUrl = feed.ArtworkUrl
                        };

                        if (!string.IsNullOrEmpty(episode.AudioUrl))
                        {
                            feed.Episodes.Add(episode);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Fallback attempt via FeedReader
                try
                {
                    var parsedFeed = await FeedReader.ReadAsync(feedUrl, cancellationToken);
                    feed.Title = parsedFeed.Title ?? "Untitled Podcast";
                    feed.WebsiteUrl = parsedFeed.Link ?? string.Empty;
                    feed.Description = parsedFeed.Description ?? string.Empty;
                    feed.ArtworkUrl = parsedFeed.ImageUrl ?? string.Empty;
                }
                catch
                {
                    throw new Exception($"Failed to parse feed from {feedUrl}: {ex.Message}", ex);
                }
            }

            feed.UnreadCount = feed.Episodes.Count;
            return feed;
        }

        private PodcastEpisode? ParseEpisodeItem(XElement item, string feedId, string feedArtworkUrl, XNamespace itunesNs)
        {
            var enclosure = item.Element("enclosure");
            string audioUrl = enclosure?.Attribute("url")?.Value ?? string.Empty;
            if (string.IsNullOrEmpty(audioUrl)) return null;

            string guid = item.Element("guid")?.Value ?? audioUrl;
            string title = item.Element("title")?.Value?.Trim() ?? "Untitled Episode";
            string description = item.Element("description")?.Value?.Trim() ?? string.Empty;
            string showNotes = item.Element(itunesNs + "summary")?.Value?.Trim() 
                               ?? item.Element("encoded")?.Value?.Trim() 
                               ?? description;

            string pubDateStr = item.Element("pubDate")?.Value ?? string.Empty;
            DateTime pubDate = DateTime.UtcNow;
            if (!string.IsNullOrEmpty(pubDateStr) && DateTime.TryParse(pubDateStr, out var parsedDate))
            {
                pubDate = parsedDate;
            }

            string durationStr = item.Element(itunesNs + "duration")?.Value ?? "0";
            double durationSeconds = ParseDurationSeconds(durationStr);

            string? epArtwork = item.Element(itunesNs + "image")?.Attribute("href")?.Value;
            if (string.IsNullOrEmpty(epArtwork)) epArtwork = feedArtworkUrl;

            // Check for podcast namespace chapters
            XNamespace podcastNs = "https://podcastindex.org/namespace/1.0";
            var chaptersElem = item.Element(podcastNs + "chapters");
            string chaptersUrl = chaptersElem?.Attribute("url")?.Value ?? string.Empty;

            return new PodcastEpisode
            {
                PodcastFeedId = feedId,
                EpisodeGuid = guid,
                Title = title,
                Description = description,
                ShowNotes = showNotes,
                PubDate = pubDate,
                DurationSeconds = durationSeconds,
                AudioUrl = audioUrl,
                ArtworkUrl = epArtwork ?? string.Empty
            };
        }

        private double ParseDurationSeconds(string durationStr)
        {
            if (double.TryParse(durationStr, out var secs)) return secs;
            if (TimeSpan.TryParse(durationStr, out var ts)) return ts.TotalSeconds;

            var parts = durationStr.Split(':');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var h) &&
                int.TryParse(parts[1], out var m) &&
                int.TryParse(parts[2], out var s))
            {
                return new TimeSpan(h, m, s).TotalSeconds;
            }
            if (parts.Length == 2 &&
                int.TryParse(parts[0], out var m2) &&
                int.TryParse(parts[1], out var s2))
            {
                return new TimeSpan(0, m2, s2).TotalSeconds;
            }
            return 0;
        }

        public List<string> ParseOpml(string opmlContent)
        {
            var urls = new List<string>();
            try
            {
                var doc = XDocument.Parse(opmlContent);
                var outlines = doc.Descendants("outline");
                foreach (var outline in outlines)
                {
                    string? xmlUrl = outline.Attribute("xmlUrl")?.Value ?? outline.Attribute("xmlurl")?.Value;
                    if (!string.IsNullOrWhiteSpace(xmlUrl) && Uri.IsWellFormedUriString(xmlUrl, UriKind.Absolute))
                    {
                        urls.Add(xmlUrl.Trim());
                    }
                }
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Invalid OPML document format: {ex.Message}", ex);
            }
            return urls.Distinct().ToList();
        }

        public string ExportOpml(IEnumerable<PodcastFeed> feeds)
        {
            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
            sb.AppendLine("<opml version=\"2.0\">");
            sb.AppendLine("  <head>");
            sb.AppendLine($"    <title>Podcast Subscriptions Export</title>");
            sb.AppendLine($"    <dateCreated>{DateTime.UtcNow:R}</dateCreated>");
            sb.AppendLine("  </head>");
            sb.AppendLine("  <body>");
            sb.AppendLine("    <outline text=\"Podcasts\" title=\"Podcasts\">");

            foreach (var feed in feeds)
            {
                string titleEsc = SecurityElement.Escape(feed.Title);
                string textEsc = SecurityElement.Escape(feed.Title);
                string xmlUrlEsc = SecurityElement.Escape(feed.FeedUrl);
                string htmlUrlEsc = SecurityElement.Escape(feed.WebsiteUrl);

                sb.AppendLine($"      <outline type=\"rss\" text=\"{titleEsc}\" title=\"{textEsc}\" xmlUrl=\"{xmlUrlEsc}\" htmlUrl=\"{htmlUrlEsc}\" />");
            }

            sb.AppendLine("    </outline>");
            sb.AppendLine("  </body>");
            sb.AppendLine("</opml>");

            return sb.ToString();
        }
    }

    internal static class SecurityElement
    {
        public static string Escape(string str)
        {
            if (string.IsNullOrEmpty(str)) return string.Empty;
            return str.Replace("&", "&amp;")
                      .Replace("<", "&lt;")
                      .Replace(">", "&gt;")
                      .Replace("\"", "&quot;")
                      .Replace("'", "&apos;");
        }
    }
}
