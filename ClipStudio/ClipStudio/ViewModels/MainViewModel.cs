using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClipStudio.Models;
using ClipStudio.Services;

namespace ClipStudio.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        public IActivityLogger Logger { get; }

        [ObservableProperty]
        private string _sourcePath = string.Empty;

        [ObservableProperty]
        private string _outputFolder = string.Empty;

        public ObservableCollection<string> Qualities { get; } = new(new[] { "1080p", "720p", "480p" });

        [ObservableProperty]
        private string _selectedQuality = "1080p";

        public ObservableCollection<ContentStyle> ContentStyles { get; } = new(Enum.GetValues<ContentStyle>());

        [ObservableProperty]
        private ContentStyle _selectedContentStyle = ContentStyle.Balanced;

        [ObservableProperty]
        private int _clipCount = 3;

        [ObservableProperty]
        private double _clipLengthMultiplier = 60.0; // 60 seconds

        [ObservableProperty]
        private bool _reviewClipsEnabled = true;

        [ObservableProperty]
        private bool _removeFillerWordsEnabled = false;

        [ObservableProperty]
        private int _progressValue = 0;

        [ObservableProperty]
        private string _statusText = "Ready";

        [ObservableProperty]
        private bool _isReviewing = false;

        public ObservableCollection<ClipCandidateViewModel> ProposedClips { get; } = new();

        private CancellationTokenSource? _cancellationTokenSource;
        private string? _downloadedFilePath;
        private List<CropTrackBuilder.CropPoint>? _cropTrack;
        private List<TranscriptionService.CutSpan>? _fillerWords;

        public MainViewModel(IActivityLogger logger)
        {
            Logger = logger;
        }

        [RelayCommand]
        private void BrowseSource()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Filter = "Video Files|*.mp4;*.mkv;*.avi;*.mov|All Files|*.*"
            };
            if (dialog.ShowDialog() == true)
            {
                SourcePath = dialog.FileName;
            }
        }

        [RelayCommand]
        private void BrowseOutput()
        {
            // Quick workaround for folder picker since native FolderBrowserDialog is winforms
            var dialog = new Microsoft.Win32.SaveFileDialog
            {
                Title = "Select Output Folder",
                FileName = "SelectFolder",
                Filter = "Directory|*.this.directory"
            };
            if (dialog.ShowDialog() == true)
            {
                OutputFolder = System.IO.Path.GetDirectoryName(dialog.FileName) ?? string.Empty;
            }
        }

        [RelayCommand]
        private async Task StartAsync()
        {
            if (string.IsNullOrWhiteSpace(SourcePath) || string.IsNullOrWhiteSpace(OutputFolder))
            {
                Logger.Log("Source and Output Folder must be specified.");
                return;
            }

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            StatusText = "Initializing...";
            ProgressValue = 0;
            IsReviewing = false;
            ProposedClips.Clear();
            _downloadedFilePath = null;
            _cropTrack = null;
            _fillerWords = null;
            string? tempWavPath = null;

            Logger.Log("Processing started...");

            try
            {
                // 1. Download or Resolve Local File
                string videoPath = SourcePath;
                if (SourcePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    StatusText = "Downloading...";
                    var ytDownloader = new YouTubeDownloader(Logger);
                    var progress = new Progress<int>(p => ProgressValue = p);
                    videoPath = await ytDownloader.DownloadVideoAsync(SourcePath, OutputFolder, SelectedQuality, progress, token);
                    _downloadedFilePath = videoPath;
                }

                ProgressValue = 0;
                StatusText = "Extracting Audio...";

                // 2. Extract Audio
                var videoAnalyzer = new VideoAnalyzer(Logger);
                tempWavPath = await videoAnalyzer.ExtractAudioAsync(videoPath, token);

                StatusText = "Analyzing Audio/Video...";
                ProgressValue = 25;

                // 3. Audio Loudness & Transcribe (for AI Candidates)
                var loudnessTask = videoAnalyzer.AnalyzeAudioLoudnessAsync(tempWavPath, token);

                List<ClipCandidate>? aiCandidates = null;
                var transcriptionService = new TranscriptionService(Logger);
                var aiService = new AIClipFinderService(Logger);

                try
                {
                    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GroqConfig.EnvVarName)))
                    {
                        Logger.Log("Warning: GROQ_API_KEY environment variable missing. Falling back to heuristic.");
                        aiCandidates = null;
                    }
                    else
                    {
                        var transcript = await transcriptionService.TranscribeAsync(tempWavPath, token);
                        aiCandidates = await aiService.GetHighlightsAsync(transcript, ClipCount, 15.0, ClipLengthMultiplier, token);

                        if (aiCandidates == null || aiCandidates.Count == 0)
                        {
                            Logger.Log("Warning: AI highlight selector returned no clips. Falling back to heuristic.");
                            aiCandidates = null;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (System.IO.FileNotFoundException ex)
                {
                    Logger.Log($"Warning: AI model missing. Falling back to heuristic. ({ex.Message})");
                    aiCandidates = null;
                }
                catch (Exception ex)
                {
                    Logger.Log($"Warning: AI highlight selector failed. Falling back to heuristic. ({ex.Message})");
                    aiCandidates = null;
                }

                var loudnessScores = await loudnessTask;
                ProgressValue = 50;

                // 4. Face Tracking (Needed for Candidate Generation if no AI, and for Cropping)
                StatusText = "Tracking Faces...";
                ProgressValue = 60;
                var faceTracker = new FaceTrackerService(Logger);
                List<FaceDetection> detections = new();
                try
                {
                    detections = await faceTracker.TrackFacesAsync(videoPath, token);
                }
                catch (System.IO.FileNotFoundException) { }

                var trackBuilder = new CropTrackBuilder();
                _cropTrack = trackBuilder.BuildTrack(detections, (loudnessScores.Count * 1.0), 30); // Approx duration

                // 5. Generate candidate clips
                StatusText = "Finding Highlights...";
                List<ClipCandidate> candidates = aiCandidates != null && aiCandidates.Count > 0
                    ? aiCandidates
                    : videoAnalyzer.GenerateCandidates(loudnessScores, detections, SelectedContentStyle, ClipCount, ClipLengthMultiplier);

                // Keep only required number
                candidates = candidates.OrderByDescending(c => c.Score).Take(ClipCount).OrderBy(c => c.StartTime).ToList();

                ProgressValue = 75;

                // 6. Filler words
                if (RemoveFillerWordsEnabled)
                {
                    StatusText = "Detecting Filler Words...";
                    var fillerService = new TranscriptionService(Logger);
                    try
                    {
                        _fillerWords = await fillerService.DetectFillerWordsAsync(tempWavPath, token);
                    }
                    catch (System.IO.FileNotFoundException) { }
                }

                ProgressValue = 90;

                foreach (var candidate in candidates)
                {
                    ProposedClips.Add(new ClipCandidateViewModel(candidate));
                }

                if (ReviewClipsEnabled)
                {
                    StatusText = "Waiting for review...";
                    IsReviewing = true;
                }
                else
                {
                    await ApproveAndRenderAsync();
                }
            }
            catch (OperationCanceledException)
            {
                ResetUI();
                Logger.Log("Operation canceled by user.");
            }
            catch (Exception ex)
            {
                StatusText = "Error occurred.";
                Logger.Log($"Error: {ex.Message}");
            }
            finally
            {
                if (tempWavPath != null && System.IO.File.Exists(tempWavPath))
                {
                    try { System.IO.File.Delete(tempWavPath); } catch { }
                }

                // If operation was canceled or we aren't reviewing, and we downloaded a file, we should delete it if we don't need it.
                // Actually, wait until rendering is finished.
            }
        }

        [RelayCommand]
        private void Cancel()
        {
            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                _cancellationTokenSource.Dispose();
                _cancellationTokenSource = null;
                StatusText = "Canceling...";
                Logger.Log("Cancellation requested.");
            }
        }

        [RelayCommand]
        private async Task ApproveAndRenderAsync()
        {
            IsReviewing = false;
            StatusText = "Rendering clips...";
            Logger.Log("Rendering approved clips...");

            var token = _cancellationTokenSource?.Token ?? CancellationToken.None;
            var creator = new ClipCreator(Logger);

            string sourceVideo = _downloadedFilePath ?? SourcePath;

            try
            {
                var clipsToRender = ProposedClips.Where(c => c.IsApproved).ToList();
                int total = clipsToRender.Count;
                int current = 0;

                foreach (var clipVM in clipsToRender)
                {
                    token.ThrowIfCancellationRequested();

                    var clip = clipVM.GetClip();
                    string outName = $"Clip_{current + 1}_{Guid.NewGuid().ToString().Substring(0,4)}.mp4";
                    string outPath = System.IO.Path.Combine(OutputFolder, outName);

                    await creator.RenderClipAsync(sourceVideo, outPath, clip, _cropTrack ?? new List<CropTrackBuilder.CropPoint>(), _fillerWords, token);

                    current++;
                    ProgressValue = 90 + (int)((current / (double)total) * 10);
                }

                StatusText = "Completed!";
                ProgressValue = 100;
                Logger.Log("Processing completed successfully.");
            }
            catch (OperationCanceledException)
            {
                ResetUI();
                Logger.Log("Rendering canceled by user.");
            }
            catch (Exception ex)
            {
                StatusText = "Error during render.";
                Logger.Log($"Render error: {ex.Message}");
            }
            finally
            {
                if (_downloadedFilePath != null && System.IO.File.Exists(_downloadedFilePath))
                {
                    try { System.IO.File.Delete(_downloadedFilePath); _downloadedFilePath = null; } catch { }
                }
            }
        }

        private void ResetUI()
        {
            ProgressValue = 0;
            StatusText = "Ready";
            IsReviewing = false;
            ProposedClips.Clear();

            if (_downloadedFilePath != null && System.IO.File.Exists(_downloadedFilePath))
            {
                try { System.IO.File.Delete(_downloadedFilePath); _downloadedFilePath = null; } catch { }
            }
        }
    }

    public partial class ClipCandidateViewModel : ObservableObject
    {
        private readonly ClipCandidate _clip;

        public ClipCandidateViewModel(ClipCandidate clip)
        {
            _clip = clip;
            IsApproved = clip.IsApproved;
        }

        [ObservableProperty]
        private bool _isApproved;

        partial void OnIsApprovedChanged(bool value)
        {
            _clip.IsApproved = value;
        }

        public string DisplayText => $"[{_clip.StartTime:hh\\:mm\\:ss} - {_clip.EndTime:hh\\:mm\\:ss}] ({_clip.Duration:F1}s) Score: {_clip.Score:F2}";

        public ClipCandidate GetClip() => _clip;
    }
}
