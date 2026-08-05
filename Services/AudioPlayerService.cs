using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows.Threading;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PodcastAggregatorStreamer.Models;

namespace PodcastAggregatorStreamer.Services
{
    public class AudioPlayerService : IDisposable
    {
        private IWavePlayer? _wavePlayer;
        private WaveStream? _audioStream;
        private VolumeSampleProvider? _volumeProvider;

        private PodcastEpisode? _currentEpisode;
        private readonly Channel<PlaybackTelemetry> _telemetryChannel;
        private readonly DispatcherTimer _telemetryTimer;

        public ChannelReader<PlaybackTelemetry> TelemetryReader => _telemetryChannel.Reader;

        public PodcastEpisode? CurrentEpisode => _currentEpisode;
        public bool IsPlaying => _wavePlayer?.PlaybackState == PlaybackState.Playing;
        public float Volume { get; private set; } = 1.0f;
        public float PlaybackSpeed { get; private set; } = 1.0f;

        public event Action<PodcastEpisode, double>? PlaybackPositionChanged;
        public event Action<PodcastEpisode>? PlaybackEnded;

        public AudioPlayerService()
        {
            var channelOptions = new BoundedChannelOptions(100)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,
                SingleReader = false
            };
            _telemetryChannel = Channel.CreateBounded<PlaybackTelemetry>(channelOptions);

            _telemetryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _telemetryTimer.Tick += OnTelemetryTick;
        }

        public async Task PlayEpisodeAsync(PodcastEpisode episode, double startPositionSeconds = 0)
        {
            Stop();

            _currentEpisode = episode;
            string mediaPath = !string.IsNullOrEmpty(episode.LocalFilePath) && File.Exists(episode.LocalFilePath)
                ? episode.LocalFilePath
                : episode.AudioUrl;

            if (string.IsNullOrEmpty(mediaPath))
                throw new InvalidOperationException("No valid audio URL or local file path for this episode.");

            await Task.Run(() =>
            {
                try
                {
                    if (mediaPath.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        mediaPath.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    {
                        _audioStream = new MediaFoundationReader(mediaPath);
                    }
                    else
                    {
                        _audioStream = new AudioFileReader(mediaPath);
                    }

                    var sampleProvider = _audioStream.ToSampleProvider();
                    _volumeProvider = new VolumeSampleProvider(sampleProvider) { Volume = Volume };

                    _wavePlayer = new WaveOutEvent
                    {
                        DesiredLatency = 150
                    };

                    _wavePlayer.Init(_volumeProvider);

                    if (startPositionSeconds > 0 && startPositionSeconds < _audioStream.TotalTime.TotalSeconds)
                    {
                        _audioStream.CurrentTime = TimeSpan.FromSeconds(startPositionSeconds);
                    }

                    _wavePlayer.PlaybackStopped += OnPlaybackStopped;
                    _wavePlayer.Play();
                }
                catch (Exception ex)
                {
                    Stop();
                    throw new InvalidOperationException($"Audio playback failed: {ex.Message}", ex);
                }
            });

            // Extract audio chapter markers if available
            ExtractChaptersIfAvailable(episode, mediaPath);

            _telemetryTimer.Start();
            EmitTelemetry();
        }

        public void Pause()
        {
            if (_wavePlayer != null && _wavePlayer.PlaybackState == PlaybackState.Playing)
            {
                _wavePlayer.Pause();
                EmitTelemetry();
            }
        }

        public void Resume()
        {
            if (_wavePlayer != null && _wavePlayer.PlaybackState == PlaybackState.Paused)
            {
                _wavePlayer.Play();
                EmitTelemetry();
            }
        }

        public void Stop()
        {
            _telemetryTimer.Stop();

            if (_wavePlayer != null)
            {
                _wavePlayer.PlaybackStopped -= OnPlaybackStopped;
                try
                {
                    _wavePlayer.Stop();
                }
                catch { }
                _wavePlayer.Dispose();
                _wavePlayer = null;
            }

            if (_audioStream != null)
            {
                try
                {
                    _audioStream.Dispose();
                }
                catch { }
                _audioStream = null;
            }

            _volumeProvider = null;
            EmitTelemetry();
        }

        public void Seek(double targetSeconds)
        {
            if (_audioStream != null)
            {
                targetSeconds = Math.Max(0, Math.Min(targetSeconds, _audioStream.TotalTime.TotalSeconds));
                _audioStream.CurrentTime = TimeSpan.FromSeconds(targetSeconds);
                EmitTelemetry();
            }
        }

        public void Skip(double deltaSeconds)
        {
            if (_audioStream != null)
            {
                double targetSeconds = _audioStream.CurrentTime.TotalSeconds + deltaSeconds;
                Seek(targetSeconds);
            }
        }

        public void SetVolume(float volume)
        {
            Volume = Math.Clamp(volume, 0.0f, 1.0f);
            if (_volumeProvider != null)
            {
                _volumeProvider.Volume = Volume;
            }
            EmitTelemetry();
        }

        public void SetPlaybackSpeed(float speed)
        {
            PlaybackSpeed = Math.Clamp(speed, 0.5f, 3.0f);
            // In MediaFoundation / WaveOut, we adjust position step speed or resampling multiplier if supported
            EmitTelemetry();
        }

        private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
        {
            _telemetryTimer.Stop();
            if (_currentEpisode != null)
            {
                PlaybackEnded?.Invoke(_currentEpisode);
            }
            EmitTelemetry();
        }

        private void OnTelemetryTick(object? sender, EventArgs e)
        {
            if (_currentEpisode != null && _audioStream != null)
            {
                double currentSecs = _audioStream.CurrentTime.TotalSeconds;
                PlaybackPositionChanged?.Invoke(_currentEpisode, currentSecs);
            }
            EmitTelemetry();
        }

        private void EmitTelemetry()
        {
            var telemetry = new PlaybackTelemetry
            {
                EpisodeId = _currentEpisode?.Id ?? string.Empty,
                EpisodeTitle = _currentEpisode?.Title ?? "No media playing",
                PodcastTitle = _currentEpisode?.PodcastFeed?.Title ?? string.Empty,
                CurrentTimeSeconds = _audioStream?.CurrentTime.TotalSeconds ?? 0,
                TotalTimeSeconds = _audioStream?.TotalTime.TotalSeconds ?? _currentEpisode?.DurationSeconds ?? 0,
                Volume = Volume,
                PlaybackSpeed = PlaybackSpeed,
                IsPlaying = IsPlaying,
                IsBuffering = false,
                ActiveChapterTitle = GetActiveChapterTitle()
            };

            if (telemetry.TotalTimeSeconds > 0)
            {
                telemetry.BufferProgressPercentage = Math.Min(100.0, (telemetry.CurrentTimeSeconds / telemetry.TotalTimeSeconds) * 100.0);
            }

            _telemetryChannel.Writer.TryWrite(telemetry);
        }

        private string GetActiveChapterTitle()
        {
            if (_currentEpisode == null || _currentEpisode.Chapters == null || _currentEpisode.Chapters.Count == 0)
                return string.Empty;

            double currentSecs = _audioStream?.CurrentTime.TotalSeconds ?? 0;
            var chapter = _currentEpisode.Chapters.FirstOrDefault(c => currentSecs >= c.StartTimeSeconds && currentSecs <= c.EndTimeSeconds);
            if (chapter != null) return chapter.Title;

            var lastPassed = _currentEpisode.Chapters.LastOrDefault(c => currentSecs >= c.StartTimeSeconds);
            return lastPassed?.Title ?? string.Empty;
        }

        private void ExtractChaptersIfAvailable(PodcastEpisode episode, string mediaPath)
        {
            if (episode.Chapters.Count > 0) return;

            var chapters = new List<EpisodeChapter>();

            // Parse chapter timestamps from show notes / description (e.g., "01:23 Intro", "00:05:30 Chapter 1")
            string textToSearch = $"{episode.Description}\n{episode.ShowNotes}";
            var timestampRegex = new Regex(@"(?:(\d{1,2}):)?(\d{2}):(\d{2})\s+[-–—]?\s*([^\r\n]+)", RegexOptions.Compiled);
            var matches = timestampRegex.Matches(textToSearch);

            foreach (Match match in matches)
            {
                int hours = match.Groups[1].Success ? int.Parse(match.Groups[1].Value) : 0;
                int mins = int.Parse(match.Groups[2].Value);
                int secs = int.Parse(match.Groups[3].Value);
                string title = match.Groups[4].Value.Trim();

                double startSecs = new TimeSpan(hours, mins, secs).TotalSeconds;
                chapters.Add(new EpisodeChapter
                {
                    Title = title,
                    StartTimeSeconds = startSecs,
                    EndTimeSeconds = startSecs + 300 // default range
                });
            }

            // Calculate chapter end times
            for (int i = 0; i < chapters.Count; i++)
            {
                if (i < chapters.Count - 1)
                {
                    chapters[i].EndTimeSeconds = chapters[i + 1].StartTimeSeconds;
                }
                else
                {
                    chapters[i].EndTimeSeconds = Math.Max(chapters[i].StartTimeSeconds + 60, episode.DurationSeconds);
                }
            }

            if (chapters.Count > 0)
            {
                episode.Chapters = chapters;
            }
        }

        public void Dispose()
        {
            Stop();
        }
    }
}
