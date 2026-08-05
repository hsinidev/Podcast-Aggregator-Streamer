using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PodcastAggregatorStreamer.Models;
using PodcastAggregatorStreamer.Services;

namespace PodcastAggregatorStreamer.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private readonly DatabaseService _databaseService;
        private readonly PodcastFeedService _feedService;
        private readonly AudioPlayerService _audioPlayerService;
        private readonly DownloadManagerService _downloadManagerService;

        private CancellationTokenSource? _telemetryCts;
        private PeriodicTimer? _syncTimer;

        [ObservableProperty]
        private ObservableCollection<PodcastFeed> _feeds = new();

        [ObservableProperty]
        private ObservableCollection<PodcastEpisode> _displayedEpisodes = new();

        [ObservableProperty]
        private PodcastFeed? _selectedFeed;

        [ObservableProperty]
        private PodcastEpisode? _selectedEpisode;

        [ObservableProperty]
        private PodcastEpisode? _currentPlayingEpisode;

        [ObservableProperty]
        private string _activeNavFilter = "All"; // "All", "Unlistened", "Downloaded", "History"

        [ObservableProperty]
        private string _searchQuery = string.Empty;

        [ObservableProperty]
        private string _newFeedUrl = string.Empty;

        [ObservableProperty]
        private bool _isBusy;

        [ObservableProperty]
        private string _statusMessage = "Ready";

        // Playback State
        [ObservableProperty]
        private bool _isPlaying;

        [ObservableProperty]
        private double _currentTimeSeconds;

        [ObservableProperty]
        private double _totalTimeSeconds;

        [ObservableProperty]
        private double _playbackProgressPercentage;

        [ObservableProperty]
        private float _volume = 0.8f;

        [ObservableProperty]
        private float _selectedSpeed = 1.0f;

        [ObservableProperty]
        private string _activeChapterTitle = string.Empty;

        [ObservableProperty]
        private ObservableCollection<EpisodeChapter> _activeChapters = new();

        [ObservableProperty]
        private ObservableCollection<float> _speedOptions = new() { 0.5f, 0.8f, 1.0f, 1.2f, 1.5f, 1.8f, 2.0f, 2.5f, 3.0f };

        // Storage & Telemetry
        [ObservableProperty]
        private string _storageUsageText = "0 MB used";

        [ObservableProperty]
        private bool _isChapterDrawerOpen;

        [ObservableProperty]
        private bool _isAddFeedDialogOpen;

        [ObservableProperty]
        private bool _isOpmlDialogOpen;

        [ObservableProperty]
        private string _opmlInputText = string.Empty;

        public MainViewModel()
        {
            _databaseService = new DatabaseService();
            _feedService = new PodcastFeedService();
            _audioPlayerService = new AudioPlayerService();
            _downloadManagerService = new DownloadManagerService(_databaseService);

            _audioPlayerService.PlaybackEnded += OnPlaybackEnded;
            _audioPlayerService.PlaybackPositionChanged += OnPlaybackPositionChanged;

            _downloadManagerService.DownloadProgressUpdated += OnDownloadProgressUpdated;
            _downloadManagerService.DownloadCompleted += OnDownloadCompleted;

            InitializeAsync();
        }

        private async void InitializeAsync()
        {
            IsBusy = true;
            StatusMessage = "Loading database subscriptions...";

            await LoadSubscriptionsAsync();
            StartTelemetryLoop();
            StartBackgroundSyncTimer();

            UpdateStorageUsage();
            IsBusy = false;
            StatusMessage = "Ready";
        }

        public async Task LoadSubscriptionsAsync()
        {
            var feedsList = await _databaseService.GetAllFeedsAsync();
            Feeds.Clear();
            foreach (var f in feedsList)
            {
                Feeds.Add(f);
            }

            FilterEpisodes();
        }

        partial void OnSearchQueryChanged(string value)
        {
            FilterEpisodes();
        }

        partial void OnSelectedFeedChanged(PodcastFeed? value)
        {
            if (value != null)
            {
                ActiveNavFilter = "Feed";
            }
            FilterEpisodes();
        }

        partial void OnActiveNavFilterChanged(string value)
        {
            if (value != "Feed")
            {
                SelectedFeed = null;
            }
            FilterEpisodes();
        }

        private void FilterEpisodes()
        {
            var allEpisodesList = Feeds.SelectMany(f => f.Episodes).ToList();
            IEnumerable<PodcastEpisode> query = allEpisodesList;

            if (SelectedFeed != null)
            {
                query = query.Where(e => e.PodcastFeedId == SelectedFeed.Id);
            }
            else
            {
                switch (ActiveNavFilter)
                {
                    case "Unlistened":
                        query = query.Where(e => !e.IsListened);
                        break;
                    case "Downloaded":
                        query = query.Where(e => e.DownloadState == EpisodeDownloadState.Downloaded);
                        break;
                    case "History":
                        query = query.Where(e => e.PlaybackPositionSeconds > 0 || e.IsListened);
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(SearchQuery))
            {
                string s = SearchQuery.Trim().ToLowerInvariant();
                query = query.Where(e => e.Title.ToLowerInvariant().Contains(s) ||
                                         e.Description.ToLowerInvariant().Contains(s) ||
                                         (e.PodcastFeed?.Title ?? "").ToLowerInvariant().Contains(s));
            }

            DisplayedEpisodes.Clear();
            foreach (var ep in query.OrderByDescending(e => e.PubDate))
            {
                DisplayedEpisodes.Add(ep);
            }
        }

        [RelayCommand]
        private async Task AddFeedAsync()
        {
            if (string.IsNullOrWhiteSpace(NewFeedUrl)) return;

            string url = NewFeedUrl.Trim();
            IsBusy = true;
            StatusMessage = $"Fetching RSS feed from {url}...";

            try
            {
                var feed = await _feedService.FetchFeedAsync(url);
                await _databaseService.SaveFeedAsync(feed);
                if (feed.Episodes.Count > 0)
                {
                    await _databaseService.SaveEpisodesAsync(feed.Id, feed.Episodes);
                }

                NewFeedUrl = string.Empty;
                IsAddFeedDialogOpen = false;
                await LoadSubscriptionsAsync();
                StatusMessage = $"Successfully subscribed to '{feed.Title}' ({feed.Episodes.Count} episodes).";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Error adding feed: {ex.Message}";
                MessageBox.Show($"Could not subscribe to podcast feed:\n{ex.Message}", "Feed Subscription Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private async Task RefreshAllFeedsAsync()
        {
            if (Feeds.Count == 0) return;

            IsBusy = true;
            StatusMessage = "Syncing podcast feeds in background...";

            int updatedCount = 0;
            foreach (var feed in Feeds.ToList())
            {
                try
                {
                    var updatedFeed = await _feedService.FetchFeedAsync(feed.FeedUrl);
                    await _databaseService.SaveFeedAsync(updatedFeed);
                    await _databaseService.SaveEpisodesAsync(feed.Id, updatedFeed.Episodes);
                    updatedCount++;
                }
                catch { }
            }

            await LoadSubscriptionsAsync();
            IsBusy = false;
            StatusMessage = $"Synced {updatedCount} feeds at {DateTime.Now:HH:mm:ss}.";
        }

        [RelayCommand]
        private async Task DeleteFeedAsync(PodcastFeed feed)
        {
            if (feed == null) return;
            var result = MessageBox.Show($"Are you sure you want to remove '{feed.Title}' and delete all local metadata?",
                "Unsubscribe Podcast", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (result == MessageBoxResult.Yes)
            {
                await _databaseService.DeleteFeedAsync(feed.Id);
                await LoadSubscriptionsAsync();
                StatusMessage = $"Unsubscribed from {feed.Title}.";
            }
        }

        [RelayCommand]
        private async Task PlayPauseEpisodeAsync(PodcastEpisode? episode)
        {
            var epToPlay = episode ?? SelectedEpisode ?? CurrentPlayingEpisode;
            if (epToPlay == null) return;

            if (CurrentPlayingEpisode?.Id == epToPlay.Id && _audioPlayerService.IsPlaying)
            {
                _audioPlayerService.Pause();
                IsPlaying = false;
                StatusMessage = $"Paused '{epToPlay.Title}'";
                return;
            }

            if (CurrentPlayingEpisode?.Id == epToPlay.Id && !_audioPlayerService.IsPlaying)
            {
                _audioPlayerService.Resume();
                IsPlaying = true;
                StatusMessage = $"Playing '{epToPlay.Title}'";
                return;
            }

            // Start playing new episode
            CurrentPlayingEpisode = epToPlay;
            ActiveChapters.Clear();
            foreach (var c in epToPlay.Chapters) ActiveChapters.Add(c);

            IsBusy = true;
            StatusMessage = $"Loading audio stream for '{epToPlay.Title}'...";

            try
            {
                await _audioPlayerService.PlayEpisodeAsync(epToPlay, epToPlay.PlaybackPositionSeconds);
                IsPlaying = true;
                _audioPlayerService.SetVolume(Volume);
                _audioPlayerService.SetPlaybackSpeed(SelectedSpeed);

                StatusMessage = $"Streaming '{epToPlay.Title}'";
            }
            catch (Exception ex)
            {
                StatusMessage = $"Playback error: {ex.Message}";
                MessageBox.Show($"Audio streaming error:\n{ex.Message}", "Playback Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void SkipBackward()
        {
            _audioPlayerService.Skip(-10);
        }

        [RelayCommand]
        private void SkipForward()
        {
            _audioPlayerService.Skip(30);
        }

        [RelayCommand]
        private void SeekPosition(double positionSeconds)
        {
            _audioPlayerService.Seek(positionSeconds);
        }

        [RelayCommand]
        private void JumpToChapter(EpisodeChapter chapter)
        {
            if (chapter != null)
            {
                _audioPlayerService.Seek(chapter.StartTimeSeconds);
                ActiveChapterTitle = chapter.Title;
            }
        }

        [RelayCommand]
        private async Task DownloadEpisodeAsync(PodcastEpisode episode)
        {
            if (episode == null) return;
            if (episode.DownloadState == EpisodeDownloadState.Downloaded)
            {
                StatusMessage = $"Episode '{episode.Title}' is already downloaded.";
                return;
            }

            var feed = Feeds.FirstOrDefault(f => f.Id == episode.PodcastFeedId);
            await _downloadManagerService.QueueDownloadAsync(episode, feed);
            StatusMessage = $"Queued download for '{episode.Title}'";
        }

        [RelayCommand]
        private async Task ToggleMarkListenedAsync(PodcastEpisode episode)
        {
            if (episode == null) return;
            bool newState = !episode.IsListened;
            await _databaseService.MarkEpisodeListenedAsync(episode.Id, newState);
            episode.IsListened = newState;

            FilterEpisodes();
            StatusMessage = newState ? "Marked as listened." : "Marked as unread.";
        }

        [RelayCommand]
        private void ToggleChapterDrawer()
        {
            IsChapterDrawerOpen = !IsChapterDrawerOpen;
        }

        [RelayCommand]
        private void OpenAddFeedDialog()
        {
            IsAddFeedDialogOpen = true;
        }

        [RelayCommand]
        private void OpenOpmlDialog()
        {
            IsOpmlDialogOpen = true;
        }

        [RelayCommand]
        private async Task ImportOpmlAsync()
        {
            if (string.IsNullOrWhiteSpace(OpmlInputText)) return;

            IsBusy = true;
            StatusMessage = "Parsing OPML catalog...";

            try
            {
                var urls = _feedService.ParseOpml(OpmlInputText);
                int importedCount = 0;
                foreach (var url in urls)
                {
                    try
                    {
                        var feed = await _feedService.FetchFeedAsync(url);
                        await _databaseService.SaveFeedAsync(feed);
                        await _databaseService.SaveEpisodesAsync(feed.Id, feed.Episodes);
                        importedCount++;
                    }
                    catch { }
                }

                IsOpmlDialogOpen = false;
                OpmlInputText = string.Empty;
                await LoadSubscriptionsAsync();
                StatusMessage = $"Successfully imported {importedCount} podcasts from OPML.";
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Error parsing OPML: {ex.Message}", "OPML Import Error", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                IsBusy = false;
            }
        }

        [RelayCommand]
        private void ExportOpml()
        {
            if (Feeds.Count == 0) return;

            string xml = _feedService.ExportOpml(Feeds);
            string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "Podcasts_Export.opml");
            File.WriteAllText(path, xml);

            MessageBox.Show($"Exported {Feeds.Count} subscriptions to OPML file:\n{path}", "OPML Export Successful", MessageBoxButton.OK, MessageBoxImage.Information);
        }

        partial void OnVolumeChanged(float value)
        {
            _audioPlayerService.SetVolume(value);
        }

        partial void OnSelectedSpeedChanged(float value)
        {
            _audioPlayerService.SetPlaybackSpeed(value);
        }

        private void StartTelemetryLoop()
        {
            _telemetryCts = new CancellationTokenSource();
            Task.Run(async () =>
            {
                var reader = _audioPlayerService.TelemetryReader;
                while (await reader.WaitToReadAsync(_telemetryCts.Token))
                {
                    while (reader.TryRead(out var telemetry))
                    {
                        Application.Current?.Dispatcher.Invoke(() =>
                        {
                            IsPlaying = telemetry.IsPlaying;
                            CurrentTimeSeconds = telemetry.CurrentTimeSeconds;
                            TotalTimeSeconds = telemetry.TotalTimeSeconds;
                            PlaybackProgressPercentage = telemetry.BufferProgressPercentage;
                            if (!string.IsNullOrEmpty(telemetry.ActiveChapterTitle))
                            {
                                ActiveChapterTitle = telemetry.ActiveChapterTitle;
                            }
                        });
                    }
                }
            }, _telemetryCts.Token);
        }

        private void StartBackgroundSyncTimer()
        {
            Task.Run(async () =>
            {
                _syncTimer = new PeriodicTimer(TimeSpan.FromMinutes(30));
                while (await _syncTimer.WaitForNextTickAsync())
                {
                    await RefreshAllFeedsAsync();
                }
            });
        }

        private async void OnPlaybackPositionChanged(PodcastEpisode episode, double currentSecs)
        {
            if (episode != null)
            {
                episode.PlaybackPositionSeconds = currentSecs;
                bool isListened = (currentSecs >= episode.DurationSeconds - 5) && episode.DurationSeconds > 0;
                await _databaseService.UpdateEpisodePlaybackPositionAsync(episode.Id, currentSecs, isListened);
            }
        }

        private void OnPlaybackEnded(PodcastEpisode episode)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                IsPlaying = false;
                StatusMessage = $"Finished playing '{episode.Title}'";
            });
        }

        private void OnDownloadProgressUpdated(DownloadTask task)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var ep = DisplayedEpisodes.FirstOrDefault(e => e.Id == task.EpisodeId);
                if (ep != null)
                {
                    ep.DownloadState = task.State;
                    ep.DownloadProgress = task.ProgressPercentage;
                }
            });
        }

        private void OnDownloadCompleted(DownloadTask task)
        {
            Application.Current?.Dispatcher.Invoke(() =>
            {
                var ep = DisplayedEpisodes.FirstOrDefault(e => e.Id == task.EpisodeId);
                if (ep != null)
                {
                    ep.DownloadState = EpisodeDownloadState.Downloaded;
                    ep.DownloadProgress = 100.0;
                    ep.LocalFilePath = task.TargetPath;
                }
                UpdateStorageUsage();
                StatusMessage = $"Downloaded '{task.Title}'";
            });
        }

        private void UpdateStorageUsage()
        {
            long bytes = _downloadManagerService.GetTotalStorageBytesUsed();
            double mb = bytes / (1024.0 * 1024.0);
            StorageUsageText = $"{mb:F1} MB used";
        }
    }
}
