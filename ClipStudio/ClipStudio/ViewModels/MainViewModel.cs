using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClipStudio.Models;
using ClipStudio.Services;
using System.Linq;

namespace ClipStudio.ViewModels
{
    public partial class MainViewModel : ObservableObject
    {
        private const double TrackLeadInSeconds = 3.0;
        private const double TrackLeadOutSeconds = 1.0;

        public IActivityLogger Logger { get; }

        [ObservableProperty]
        private string _sourcePath = string.Empty;

        [ObservableProperty]
        private string _outputFolder = string.Empty;

        public ObservableCollection<string> Qualities { get; } = new(new[] { "1080p", "720p", "480p" });

        [ObservableProperty]
        private string _selectedQuality = "1080p";

        public ObservableCollection<string> Encoders { get; } = new(new[] { "Auto", "CPU", "NVENC", "QSV", "AMF" });

        [ObservableProperty]
        private string _selectedEncoder = "Auto";

        public ObservableCollection<ContentStyle> ContentStyles { get; } = new(Enum.GetValues<ContentStyle>());

        [ObservableProperty]
        private ContentStyle _selectedContentStyle = ContentStyle.Balanced;

        [ObservableProperty]
        private int _clipCount = 3;

        [ObservableProperty]
        private double _clipLengthMultiplier = 60.0; // 60 seconds

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ManualLengthEnabled))]
        private bool _autoClipEnabled = true;

        public bool ManualLengthEnabled => !AutoClipEnabled;

        [ObservableProperty]
        private bool _reviewClipsEnabled = true;

        [ObservableProperty]
        private bool _removeFillerWordsEnabled = false;

        [ObservableProperty]
        private bool _captionsEnabled = true;

        [ObservableProperty]
        private bool _useGpuForTranscription = true;

        public ObservableCollection<CaptionStyle> CaptionStyleOptions { get; } = new(CaptionStyles.All);

        [ObservableProperty]
        private CaptionStyle _selectedCaptionStyle = CaptionStyles.Default;

        [ObservableProperty]
        private int _progressValue = 0;

        [ObservableProperty]
        private string _statusText = "Ready";

        private string? _currentSourceKey;

        [ObservableProperty]
        private bool _isReviewing = false;

        public ObservableCollection<ClipCandidateViewModel> ProposedClips { get; } = new();

        private CancellationTokenSource? _cancellationTokenSource;
        private string? _downloadedFilePath;
        private bool _isRendering = false;

        private const string DefaultOutputFolderName = "ClipStudio";
        private const string FallbackOutputFolderName = "output";

        private static string GetDefaultOutputFolder()
        {
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            return string.IsNullOrWhiteSpace(videos)
                ? System.IO.Path.Combine(AppContext.BaseDirectory, FallbackOutputFolderName)
                : System.IO.Path.Combine(videos, DefaultOutputFolderName);
        }

        private bool TryEnsureFolder(string path)
        {
            try
            {
                System.IO.Directory.CreateDirectory(path);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Log($"Error creating output folder '{path}': {ex.Message}");
                return false;
            }
        }

        [ObservableProperty]
        private bool _reuseSavedData = true;

        public MainViewModel(IActivityLogger logger)
        {
            Logger = logger;
            OutputFolder = GetDefaultOutputFolder();
            TryEnsureFolder(OutputFolder);
            try
            {
                TempPaths.CleanupStale();
            }
            catch
            {
            }
            try
            {
                ProjectStore.CleanupOld(TimeSpan.FromDays(60));
            }
            catch
            {
            }
        }

        [RelayCommand]
        private void ClearSavedData()
        {
            if (_isRendering) return;

            var result = System.Windows.MessageBox.Show(
                "Delete all saved transcripts?",
                "Clear Saved Data",
                System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);

            if (result == System.Windows.MessageBoxResult.Yes)
            {
                if (ProjectStore.ClearAll())
                {
                    Logger.Log("Saved data cleared.");
                }
                else
                {
                    Logger.Log("Could not clear saved data.");
                }
            }
        }

        private void DeleteDownloadedFile()
        {
            if (_downloadedFilePath != null)
            {
                try
                {
                    if (System.IO.File.Exists(_downloadedFilePath))
                    {
                        System.IO.File.Delete(_downloadedFilePath);
                    }
                }
                catch
                {
                }
                finally
                {
                    _downloadedFilePath = null;
                }
            }
        }

        private (int Width, int Height, double Fps) ReadVideoInfo(string path)
        {
            try
            {
                using var capture = new OpenCvSharp.VideoCapture(path);
                if (!capture.IsOpened())
                {
                    Logger.Log($"Warning: Could not open video at {path} for info extraction. Using default 1920x1080@30fps.");
                    return (1920, 1080, 30.0);
                }

                int width = capture.FrameWidth;
                int height = capture.FrameHeight;
                double fps = capture.Fps;

                if (width <= 0 || height <= 0 || fps <= 0)
                {
                    Logger.Log($"Warning: Invalid video info extracted from {path} (W:{width}, H:{height}, FPS:{fps}). Using default 1920x1080@30fps.");
                    return (1920, 1080, 30.0);
                }

                return (width, height, fps);
            }
            catch (Exception ex)
            {
                Logger.Log($"Warning: Error reading video info from {path} ({ex.Message}). Using default 1920x1080@30fps.");
                return (1920, 1080, 30.0);
            }
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
            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "Select Output Folder"
            };

            if (System.IO.Directory.Exists(OutputFolder))
            {
                dialog.InitialDirectory = OutputFolder;
            }

            if (dialog.ShowDialog() == true)
            {
                OutputFolder = dialog.FolderName;
            }
        }

        [RelayCommand]
        private async Task StartAsync()
        {
            _currentSourceKey = null;

            if (string.IsNullOrWhiteSpace(SourcePath))
            {
                Logger.Log("Source must be specified.");
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputFolder))
            {
                OutputFolder = GetDefaultOutputFolder();
                Logger.Log($"No output folder selected; using default: {OutputFolder}");
            }

            try
            {
                OutputFolder = System.IO.Path.GetFullPath(OutputFolder.Trim());
                SourcePath = SourcePath.Trim();
                if (!SourcePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                {
                    SourcePath = System.IO.Path.GetFullPath(SourcePath);
                }
            }
            catch (Exception ex) when (ex is ArgumentException || ex is NotSupportedException || ex is System.IO.PathTooLongException)
            {
                Logger.Log($"Invalid path: {ex.Message}");
                return;
            }

            if (!TryEnsureFolder(OutputFolder))
            {
                return;
            }

            if (!SourcePath.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                if (!System.IO.File.Exists(SourcePath))
                {
                    Logger.Log($"Source file not found: {SourcePath}");
                    return;
                }
            }

            if (_isRendering)
            {
                Logger.Log("A render is in progress.");
                return;
            }

            DeleteDownloadedFile();

            _cancellationTokenSource = new CancellationTokenSource();
            var token = _cancellationTokenSource.Token;

            StatusText = "Initializing...";
            ProgressValue = 0;
            IsReviewing = false;
            ProposedClips.Clear();
            _downloadedFilePath = null;
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

                string? sourceKey = ProjectStore.GetSourceKey(videoPath, _downloadedFilePath != null);
                _currentSourceKey = sourceKey;

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
                TranscriptionResult? transcription = null;

                bool haveKey = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(GroqConfig.EnvVarName));

                if (haveKey || RemoveFillerWordsEnabled || CaptionsEnabled)
                {
                    try
                    {
                        var fp = transcriptionService.GetModelFingerprint();
                        if (ReuseSavedData && sourceKey != null)
                        {
                            transcription = ProjectStore.TryLoadTranscript(sourceKey, fp.Name, fp.Size, RemoveFillerWordsEnabled, false);
                            if (transcription != null)
                            {
                                Logger.Log($"Loaded saved transcript ({transcription.Words.Count} words, {transcription.Segments.Count} segments); skipping Whisper");
                            }
                        }
                        if (transcription == null)
                        {
                            transcription = await transcriptionService.TranscribeAsync(tempWavPath, RemoveFillerWordsEnabled, UseGpuForTranscription, false, token);
                            if (sourceKey != null)
                            {
                                ProjectStore.SaveTranscript(sourceKey, fp.Name, fp.Size, RemoveFillerWordsEnabled, false, transcription);
                                Logger.Log("Saved transcript for reuse");
                            }
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (System.IO.FileNotFoundException ex)
                    {
                        Logger.Log($"Warning: AI model missing. ({ex.Message})");
                        transcription = null;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: Transcription failed. ({ex.Message})");
                        transcription = null;
                    }
                }

                if (!haveKey)
                {
                    Logger.Log("Warning: GROQ_API_KEY environment variable missing. Falling back to heuristic.");
                    aiCandidates = null;
                }
                else if (transcription != null)
                {
                    try
                    {
                        if (AutoClipEnabled)
                        {
                            Logger.Log("AI Automated Clip mode is ON (AI picks length 15-60s).");
                        }
                        else
                        {
                            Logger.Log("AI Automated Clip mode is OFF.");
                        }

                        aiService.CacheSourceKey = sourceKey;
                        aiService.ReuseSavedPicks = ReuseSavedData;

                        aiCandidates = await aiService.GetHighlightsAsync(transcription.Segments, ClipCount, 15.0, ClipLengthMultiplier, AutoClipEnabled, token);

                        if (aiCandidates == null || aiCandidates.Count == 0)
                        {
                            Logger.Log("Warning: AI highlight selector returned no clips. Falling back to heuristic.");
                            aiCandidates = null;
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: AI highlight selector failed. Falling back to heuristic. ({ex.Message})");
                        aiCandidates = null;
                    }
                }
                else
                {
                    aiCandidates = null;
                }

                var loudnessScores = await loudnessTask;
                ProgressValue = 50;

                // 4. Face Tracking (Needed for Candidate Generation if no AI)
                List<FaceDetection> detections = new();
                if (aiCandidates == null || aiCandidates.Count == 0)
                {
                    StatusText = "Tracking Faces...";
                    ProgressValue = 60;
                    var faceTracker = new FaceTrackerService(Logger);
                    try
                    {
                        detections = await faceTracker.TrackFacesAsync(videoPath, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: face tracking failed ({ex.Message}); continuing without face data.");
                        detections = new List<FaceDetection>();
                    }
                }

                // 5. Generate candidate clips
                StatusText = "Finding Highlights...";
                List<ClipCandidate> candidates = aiCandidates != null && aiCandidates.Count > 0
                    ? aiCandidates
                    : (AutoClipEnabled
                        ? videoAnalyzer.GenerateAutoLengthCandidates(loudnessScores, detections, SelectedContentStyle, ClipCount)
                        : videoAnalyzer.GenerateCandidates(loudnessScores, detections, SelectedContentStyle, ClipCount, ClipLengthMultiplier));

                // Keep only required number
                candidates = candidates.OrderByDescending(c => c.Score).Take(ClipCount).OrderBy(c => c.StartTime).ToList();

                ProgressValue = 75;

                ProgressValue = 90;

                foreach (var candidate in candidates)
                {
                    candidate.OriginalStartTime = candidate.StartTime;
                    candidate.OriginalEndTime = candidate.EndTime;
                    ProposedClips.Add(new ClipCandidateViewModel(candidate));
                }

                if (ReuseSavedData && _currentSourceKey != null)
                {
                    var savedEditsFile = ProjectStore.TryLoadEdits(_currentSourceKey);
                    if (savedEditsFile != null)
                    {
                        int restoredCount = 0;
                        foreach (var vm in ProposedClips)
                        {
                            var candidate = vm.GetClip();
                            var saved = savedEditsFile.Edits.FirstOrDefault(e =>
                                Math.Abs((e.OriginalStart - candidate.OriginalStartTime).TotalSeconds) <= 0.05 &&
                                Math.Abs((e.OriginalEnd - candidate.OriginalEndTime).TotalSeconds) <= 0.05);

                            if (saved != null)
                            {
                                var normalizedRanges = ClipEditMath.NormalizeRanges(saved.DeletedRanges, saved.Start, saved.End);
                                if (ClipEditMath.TrySetTrim(candidate, saved.Start, saved.End))
                                {
                                    candidate.StartTime = saved.Start;
                                    candidate.EndTime = saved.End;
                                    candidate.DeletedRanges = normalizedRanges;
                                    candidate.EditorWords = saved.EditorWords?.ToList();
                                    if (!string.IsNullOrWhiteSpace(saved.Transcript))
                                    {
                                        candidate.Transcript = saved.Transcript;
                                    }
                                    vm.RefreshFromClip();
                                    restoredCount++;
                                }
                            }
                        }

                        if (restoredCount > 0)
                        {
                            Logger.Log($"Restored saved edits for {restoredCount} clip(s)");
                        }
                    }
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
                DeleteDownloadedFile();
                ProgressValue = 0;
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
            if (IsReviewing)
            {
                ResetUI();
                if (_cancellationTokenSource != null)
                {
                    _cancellationTokenSource.Dispose();
                    _cancellationTokenSource = null;
                }
                Logger.Log("Review canceled.");
                return;
            }

            if (!StartCommand.IsRunning && !ApproveAndRenderCommand.IsRunning) return;

            if (_cancellationTokenSource != null && !_cancellationTokenSource.IsCancellationRequested)
            {
                _cancellationTokenSource.Cancel();
                StatusText = "Canceling...";
                Logger.Log("Cancellation requested.");
            }
        }

        [RelayCommand]
        private void EditClip(ClipCandidateViewModel? item)
        {
            if (item == null || _isRendering) return;

            string sourceVideo = _downloadedFilePath ?? SourcePath;
            if (!System.IO.File.Exists(sourceVideo))
            {
                Logger.Log("Source video not found; cannot edit.");
                return;
            }

            var editorVm = new ClipEditorViewModel(item.GetClip(), sourceVideo, UseGpuForTranscription, Logger);
            var editorWin = new ClipEditorWindow { Owner = System.Windows.Application.Current.MainWindow, DataContext = editorVm };

            bool? result = editorWin.ShowDialog();

            if (_currentSourceKey != null && (result == true || item.GetClip().EditorWords != null))
            {
                ProjectStore.SaveEdit(_currentSourceKey, item.GetClip());
            }

            item.RefreshFromClip();
        }

        [RelayCommand]
        private async Task ApproveAndRenderAsync()
        {
            _isRendering = true;
            IsReviewing = false;
            StatusText = "Rendering clips...";
            Logger.Log("Rendering approved clips...");

            var token = _cancellationTokenSource?.Token ?? CancellationToken.None;
            var creator = new ClipCreator(Logger);

            string sourceVideo = _downloadedFilePath ?? SourcePath;

            var styleForRender = SelectedCaptionStyle;

            try
            {
                var clipsToRender = ProposedClips.Where(c => c.IsApproved).ToList();
                int total = clipsToRender.Count;
                int current = 0;

                var info = ReadVideoInfo(sourceVideo);
                var faceTracker = new FaceTrackerService(Logger);
                var trackBuilder = new CropTrackBuilder();
                var videoAnalyzer = new VideoAnalyzer(Logger);
                var transcriptionService = new TranscriptionService(Logger);

                foreach (var clipVM in clipsToRender)
                {
                    token.ThrowIfCancellationRequested();

                    var clip = clipVM.GetClip();

                    var manual = ClipEditMath.NormalizeRanges(clip.DeletedRanges, clip.StartTime, clip.EndTime);
                    if (ClipEditMath.KeptDuration(clip.StartTime, clip.EndTime, manual).TotalSeconds < ClipEditMath.MinClipSeconds)
                    {
                        Logger.Log($"Skipping clip [{clip.StartTime:hh\\:mm\\:ss} - {clip.EndTime:hh\\:mm\\:ss}]: nothing left after cuts");
                        current++;
                        ProgressValue = 90 + (int)((current / (double)total) * 10);
                        continue;
                    }

                    string outName = $"Clip_{current + 1}_{Guid.NewGuid().ToString().Substring(0,4)}.mp4";
                    string outPath = System.IO.Path.Combine(OutputFolder, outName);

                    double winStart = Math.Max(0, clip.StartTime.TotalSeconds - TrackLeadInSeconds);
                    double winEnd = clip.EndTime.TotalSeconds + TrackLeadOutSeconds;

                    StatusText = $"Tracking faces (clip {current + 1}/{total})...";

                    List<FaceDetection> detections;
                    try
                    {
                        detections = await faceTracker.TrackFacesAsync(sourceVideo, new[] { (winStart, winEnd) }, token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        Logger.Log($"Warning: face tracking failed ({ex.Message}); using a center crop.");
                        detections = new List<FaceDetection>();
                    }

                    var clipTrack = trackBuilder.BuildClipTrack(detections, winStart, winEnd, info.Fps, info.Width, info.Height);

                    List<TranscriptionService.CutSpan>? localFillerWords = null;
                    List<WordTiming>? wordsForRender = null;
                    string? tempClipWavPath = null;

                    try
                    {
                        if (CaptionsEnabled || RemoveFillerWordsEnabled)
                        {
                            StatusText = $"Extracting audio for clip {current + 1}/{total}...";
                            tempClipWavPath = await videoAnalyzer.ExtractAudioAsync(sourceVideo, token, clip.StartTime, clip.EndTime);

                            StatusText = $"Transcribing clip {current + 1}/{total}...";
                            var clipTranscription = await transcriptionService.TranscribeAsync(tempClipWavPath, RemoveFillerWordsEnabled, UseGpuForTranscription, true, token);

                            var shiftedWords = new List<WordTiming>(clipTranscription.Words.Count);
                            foreach (var word in clipTranscription.Words)
                            {
                                shiftedWords.Add(word with
                                {
                                    Start = word.Start.Add(clip.StartTime),
                                    End = word.End.Add(clip.StartTime)
                                });
                            }

                            if (CaptionsEnabled)
                            {
                                wordsForRender = shiftedWords;
                            }

                            if (RemoveFillerWordsEnabled)
                            {
                                StatusText = $"Detecting Filler Words for clip {current + 1}/{total}...";
                                localFillerWords = transcriptionService.DetectFillerSpans(shiftedWords);
                            }
                        }

                        IEnumerable<TimeRange>? fillerRanges = localFillerWords?.Select(s => new TimeRange(s.Start, s.End));
                        var merged = ClipEditMath.MergeRanges(fillerRanges ?? Enumerable.Empty<TimeRange>(), manual, clip.StartTime, clip.EndTime);

                        if (ClipEditMath.KeptDuration(clip.StartTime, clip.EndTime, merged).TotalSeconds < ClipEditMath.MinClipSeconds)
                        {
                            Logger.Log($"Skipping clip [{clip.StartTime:hh\\:mm\\:ss} - {clip.EndTime:hh\\:mm\\:ss}]: nothing left after cuts");
                            current++;
                            ProgressValue = 90 + (int)((current / (double)total) * 10);
                            continue;
                        }

                        if (manual.Count > 0)
                        {
                            Logger.Log($"Applying {manual.Count} manual cut(s) to clip {current + 1}/{total}");
                        }

                        List<TranscriptionService.CutSpan>? mergedSpans = merged.Count > 0
                            ? merged.Select(r => new TranscriptionService.CutSpan { Start = r.Start, End = r.End }).ToList()
                            : null;

                        StatusText = $"Rendering clip {current + 1}/{total}...";

                        await creator.RenderClipAsync(
                            sourceVideo,
                            outPath,
                            clip,
                            clipTrack,
                            mergedSpans,
                            token,
                            wordsForRender,
                            styleForRender,
                            SelectedEncoder,
                            msg => StatusText = msg);
                    }
                    finally
                    {
                        if (tempClipWavPath != null && System.IO.File.Exists(tempClipWavPath))
                        {
                            try { System.IO.File.Delete(tempClipWavPath); } catch { }
                        }
                    }

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
                DeleteDownloadedFile();
                _isRendering = false;
            }
        }

        private void ResetUI()
        {
            ProgressValue = 0;
            StatusText = "Ready";
            IsReviewing = false;
            ProposedClips.Clear();

            DeleteDownloadedFile();
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

        public void RefreshFromClip()
        {
            OnPropertyChanged(nameof(DisplayText));
            OnPropertyChanged(nameof(Reason));
            OnPropertyChanged(nameof(FullTranscript));
            OnPropertyChanged(nameof(Preview));
            OnPropertyChanged(nameof(HasReason));
            OnPropertyChanged(nameof(HasPreview));
            OnPropertyChanged(nameof(HasEdits));
            OnPropertyChanged(nameof(EditSummary));
        }

        public string DisplayText => $"[{_clip.StartTime:hh\\:mm\\:ss} - {_clip.EndTime:hh\\:mm\\:ss}] ({_clip.Duration:F1}s) Score: {_clip.Score:F2}";

        public string Reason => _clip.Reason;

        public string FullTranscript => _clip.Transcript;

        public string Preview
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_clip.Transcript))
                    return string.Empty;

                var collapsed = System.Text.RegularExpressions.Regex.Replace(_clip.Transcript, @"\s+", " ").Trim();
                if (collapsed.Length > 140)
                    return collapsed.Substring(0, 140) + "...";
                return collapsed;
            }
        }

        public bool HasReason => !string.IsNullOrWhiteSpace(Reason);
        public bool HasPreview => !string.IsNullOrWhiteSpace(Preview);

        public bool HasEdits => _clip.DeletedRanges != null && _clip.DeletedRanges.Count > 0;

        public string EditSummary
        {
            get {
                int count = _clip.DeletedRanges?.Count ?? 0;
                if (count == 0) return string.Empty;
                return count == 1 ? "1 cut" : $"{count} cuts";
            }
        }

        public ClipCandidate GetClip() => _clip;
    }
}
