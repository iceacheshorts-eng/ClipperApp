using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ClipStudio.Models;
using ClipStudio.Services;

namespace ClipStudio.ViewModels
{
    public enum EditorMode
    {
        DeleteWords,
        DeleteRange,
        SetStart,
        SetEnd
    }

    public partial class EditorWordItem : ObservableObject
    {
        [ObservableProperty]
        private string _text = string.Empty;

        [ObservableProperty]
        private TimeSpan _start;

        [ObservableProperty]
        private TimeSpan _end;

        [ObservableProperty]
        private bool _estimated;

        [ObservableProperty]
        private bool _isDeleted;

        [ObservableProperty]
        private bool _isOutside;

        [ObservableProperty]
        private bool _isAnchor;

        public WordTiming OriginalTiming { get; init; } = null!;
    }

    public partial class ClipEditorViewModel : ObservableObject
    {
        private readonly ClipCandidate _clip;
        private readonly string _sourceVideoPath;
        private readonly bool _useGpu;
        private readonly IActivityLogger _logger;
        private readonly VideoAnalyzer _videoAnalyzer;
        private readonly TranscriptionService _transcriptionService;

        private CancellationTokenSource? _cts;

        // Working states
        private TimeSpan _workingStart;
        private TimeSpan _workingEnd;
        private EditorWordItem? _rangeAnchor;

        public event Action<bool>? CloseRequested;

        public ObservableCollection<EditorWordItem> Words { get; } = new();

        [ObservableProperty]
        private EditorMode _mode = EditorMode.DeleteWords;

        partial void OnModeChanged(EditorMode value)
        {
            if (value != EditorMode.DeleteRange)
            {
                _rangeAnchor = null;
                StatusText = "Ready.";
            }
            else
            {
                StatusText = "Click the first word of the range.";
                if (_rangeAnchor != null)
                {
                    _rangeAnchor.IsAnchor = false;
                    _rangeAnchor = null;
                }
            }
        }

        [ObservableProperty]
        private bool _isLoading;

        [ObservableProperty]
        private string _statusText = "Initializing...";

        [ObservableProperty]
        private string _warningText = string.Empty;

        [ObservableProperty]
        private double _originalDuration;

        [ObservableProperty]
        private double _editedDuration;

        [ObservableProperty]
        private int _cutCount;

        public ClipEditorViewModel(ClipCandidate clip, string sourceVideoPath, bool useGpu, IActivityLogger logger)
        {
            _clip = clip;
            _sourceVideoPath = sourceVideoPath;
            _useGpu = useGpu;
            _logger = logger;

            _videoAnalyzer = new VideoAnalyzer(logger);
            _transcriptionService = new TranscriptionService(logger);

            _workingStart = _clip.StartTime;
            _workingEnd = _clip.EndTime;
            OriginalDuration = (_clip.EndTime - _clip.StartTime).TotalSeconds;

            UpdateInfoText();
        }

        public async Task LoadAsync()
        {
            IsLoading = true;
            StatusText = "Loading transcript...";
            _cts = new CancellationTokenSource();

            try
            {
                if (_clip.EditorWords == null)
                {
                    var loadStart = TimeSpan.FromSeconds(Math.Max(0, _clip.StartTime.TotalSeconds - 10));
                    var loadEnd = _clip.EndTime.Add(TimeSpan.FromSeconds(10));

                    StatusText = "Extracting audio...";
                    string wavPath = await _videoAnalyzer.ExtractAudioAsync(_sourceVideoPath, _cts.Token, loadStart, loadEnd);

                    try
                    {
                        StatusText = "Transcribing with word-level timestamps...";
                        var result = await _transcriptionService.TranscribeAsync(wavPath, true, _useGpu, true, _cts.Token);

                        var shiftedWords = result.Words.Select(w => new WordTiming(
                            w.Text,
                            w.Start.Add(loadStart),
                            w.End.Add(loadStart),
                            w.Estimated)).ToList();

                        _clip.EditorWords = shiftedWords;
                    }
                    finally
                    {
                        if (System.IO.File.Exists(wavPath))
                            System.IO.File.Delete(wavPath);
                    }
                }

                BuildWords();

                int estimatedCount = _clip.EditorWords.Count(w => w.Estimated);
                if (_clip.EditorWords.Count > 0 && (double)estimatedCount / _clip.EditorWords.Count > 0.2)
                {
                    WarningText = "Some word timings are estimated; cuts may be slightly off.";
                }

                StatusText = "Ready.";
            }
            catch (OperationCanceledException)
            {
                StatusText = "Loading canceled.";
            }
            catch (Exception ex)
            {
                _logger.Log($"Error loading clip editor: {ex.Message}");
                StatusText = $"Error: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }

        public void CancelLoading()
        {
            _cts?.Cancel();
        }

        private void BuildWords()
        {
            Words.Clear();
            if (_clip.EditorWords == null) return;

            foreach (var w in _clip.EditorWords)
            {
                bool isDeleted = false;
                var midpoint = w.Start + TimeSpan.FromSeconds((w.End - w.Start).TotalSeconds / 2.0);

                if (_clip.DeletedRanges != null)
                {
                    foreach (var r in _clip.DeletedRanges)
                    {
                        if (midpoint >= r.Start && midpoint <= r.End)
                        {
                            isDeleted = true;
                            break;
                        }
                    }
                }

                var item = new EditorWordItem
                {
                    Text = w.Text,
                    Start = w.Start,
                    End = w.End,
                    Estimated = w.Estimated,
                    IsDeleted = isDeleted,
                    OriginalTiming = w
                };

                Words.Add(item);
            }

            RecomputeIsOutside();
            UpdateInfoText();
        }

        private void RecomputeIsOutside()
        {
            foreach (var w in Words)
            {
                w.IsOutside = w.End <= _workingStart || w.Start >= _workingEnd;
            }
        }

        private void UpdateInfoText()
        {
            var ranges = Words.Where(w => w.IsDeleted && !w.IsOutside)
                              .Select(w => new TimeRange(w.Start, w.End))
                              .ToList();

            var normalized = ClipEditMath.NormalizeRanges(ranges, _workingStart, _workingEnd);
            EditedDuration = ClipEditMath.KeptDuration(_workingStart, _workingEnd, normalized).TotalSeconds;
            CutCount = normalized.Count;
        }

        [RelayCommand]
        private void WordClick(EditorWordItem? word)
        {
            if (word == null) return;

            if (Mode == EditorMode.DeleteWords)
            {
                if (word.IsOutside) return;
                word.IsDeleted = !word.IsDeleted;
                UpdateInfoText();
                StatusText = "Ready.";
            }
            else if (Mode == EditorMode.DeleteRange)
            {
                if (_rangeAnchor == null)
                {
                    _rangeAnchor = word;
                    _rangeAnchor.IsAnchor = true;
                    StatusText = "Click the last word of the range.";
                }
                else
                {
                    var startWord = _rangeAnchor.Start <= word.Start ? _rangeAnchor : word;
                    var endWord = _rangeAnchor.Start <= word.Start ? word : _rangeAnchor;

                    int count = 0;
                    foreach (var w in Words)
                    {
                        if (w.Start >= startWord.Start && w.Start <= endWord.Start && !w.IsOutside)
                        {
                            w.IsDeleted = true;
                            count++;
                        }
                    }

                    _rangeAnchor.IsAnchor = false;
                    _rangeAnchor = null;
                    UpdateInfoText();
                    StatusText = $"Deleted {count} words.";
                }
            }
            else if (Mode == EditorMode.SetStart)
            {
                if (word.Start >= _workingEnd)
                {
                    StatusText = "Start time must be before end time.";
                    return;
                }

                var oldStart = _workingStart;
                _workingStart = word.Start;

                if (!ValidateDuration())
                {
                    _workingStart = oldStart;
                    StatusText = $"Clip must be between {ClipEditMath.MinClipSeconds}s and {ClipEditMath.MaxClipSeconds}s.";
                    return;
                }

                RecomputeIsOutside();
                UpdateInfoText();
                StatusText = "Ready.";
            }
            else if (Mode == EditorMode.SetEnd)
            {
                if (word.End <= _workingStart)
                {
                    StatusText = "End time must be after start time.";
                    return;
                }

                var oldEnd = _workingEnd;
                _workingEnd = word.End;

                if (!ValidateDuration())
                {
                    _workingEnd = oldEnd;
                    StatusText = $"Clip must be between {ClipEditMath.MinClipSeconds}s and {ClipEditMath.MaxClipSeconds}s.";
                    return;
                }

                RecomputeIsOutside();
                UpdateInfoText();
                StatusText = "Ready.";
            }
        }

        private bool ValidateDuration()
        {
            var ranges = Words.Where(w => w.IsDeleted && !w.IsOutside)
                              .Select(w => new TimeRange(w.Start, w.End))
                              .ToList();
            var normalized = ClipEditMath.NormalizeRanges(ranges, _workingStart, _workingEnd);
            var kept = ClipEditMath.KeptDuration(_workingStart, _workingEnd, normalized).TotalSeconds;

            var total = (_workingEnd - _workingStart).TotalSeconds;

            return total >= ClipEditMath.MinClipSeconds && total <= ClipEditMath.MaxClipSeconds;
        }

        [RelayCommand]
        private void MarkFillers()
        {
            if (Words.Count == 0) return;

            var activeWords = Words.Select(w => w.OriginalTiming).ToList();
            var fillerSpans = _transcriptionService.DetectFillerSpans(activeWords);

            int count = 0;
            foreach (var w in Words)
            {
                if (w.IsOutside) continue;

                var length = (w.End - w.Start).TotalSeconds;

                foreach (var span in fillerSpans)
                {
                    var overlapStart = TimeSpan.FromSeconds(Math.Max(w.Start.TotalSeconds, span.Start.TotalSeconds));
                    var overlapEnd = TimeSpan.FromSeconds(Math.Min(w.End.TotalSeconds, span.End.TotalSeconds));

                    if (overlapEnd > overlapStart)
                    {
                        var overlapDuration = (overlapEnd - overlapStart).TotalSeconds;
                        if (overlapDuration >= length * 0.5)
                        {
                            w.IsDeleted = true;
                            count++;
                            break;
                        }
                    }
                }
            }

            UpdateInfoText();
            StatusText = $"Marked {count} filler words.";
        }

        [RelayCommand]
        private void Reset()
        {
            _workingStart = _clip.StartTime;
            _workingEnd = _clip.EndTime;
            _rangeAnchor = null;
            Mode = EditorMode.DeleteWords;
            BuildWords();
            StatusText = "Ready.";
        }

        [RelayCommand(CanExecute = nameof(CanApply))]
        private void Apply()
        {
            var ranges = Words.Where(w => w.IsDeleted && !w.IsOutside)
                              .Select(w => new TimeRange(w.Start, w.End))
                              .ToList();

            var normalized = ClipEditMath.NormalizeRanges(ranges, _workingStart, _workingEnd);
            var backupRanges = _clip.DeletedRanges;
            _clip.DeletedRanges = normalized;

            if (!ClipEditMath.TrySetTrim(_clip, _workingStart, _workingEnd))
            {
                _clip.DeletedRanges = backupRanges;
                StatusText = $"That trim is not allowed ({ClipEditMath.MinClipSeconds}-{ClipEditMath.MaxClipSeconds} s).";
                return;
            }

            var activeWords = Words.Where(w => !w.IsOutside && !w.IsDeleted).Select(w => w.Text.Trim());
            _clip.Transcript = string.Join(" ", activeWords).Trim();

            CloseRequested?.Invoke(true);
        }

        private bool CanApply() => !IsLoading && Words.Count > 0;

        [RelayCommand]
        private void Cancel()
        {
            CloseRequested?.Invoke(false);
        }

        partial void OnIsLoadingChanged(bool value)
        {
            ApplyCommand.NotifyCanExecuteChanged();
        }
    }
}
