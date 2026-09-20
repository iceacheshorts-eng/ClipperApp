using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using System.Windows.Shapes;
using System.Windows.Media;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Globalization;
using ClipStudio.ViewModels;

namespace ClipStudio
{
    public class EnumToBooleanConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            return value?.Equals(parameter) ?? false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isChecked && isChecked)
            {
                return parameter;
            }
            return Binding.DoNothing;
        }
    }

    public partial class ClipEditorWindow : Window
    {
        private DispatcherTimer _timer;
        private DateTime _lastSeekCutTime = DateTime.MinValue;
        private bool _isPlaying = false;
        private bool _mediaReady = false;
        private ClipEditorViewModel? _vm;

        public ClipEditorWindow()
        {
            InitializeComponent();
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(40)
            };
            _timer.Tick += Timer_Tick;
        }

        private async void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (DataContext is ClipEditorViewModel vm)
            {
                _vm = vm;
                vm.CloseRequested += (result) =>
                {
                    DialogResult = result;
                };

                vm.PreviewStateChanged += Vm_PreviewStateChanged;
                vm.CurrentWordChanged += Vm_CurrentWordChanged;

                if (vm.SourceUri != null)
                {
                    Player.Source = vm.SourceUri;
                }

                await vm.LoadAsync();
            }
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            _timer.Stop();
            _mediaReady = false;
            Player.Stop();
            Player.Close();
            Player.Source = null;

            if (_vm != null)
            {
                _vm.PreviewStateChanged -= Vm_PreviewStateChanged;
                _vm.CurrentWordChanged -= Vm_CurrentWordChanged;

                if (_vm.IsLoading)
                {
                    _vm.CancelLoading();
                }
            }
        }

        private void Vm_PreviewStateChanged()
        {
            if (_vm == null) return;

            SeekSlider.Minimum = _vm.WorkingStart.TotalSeconds;
            SeekSlider.Maximum = _vm.WorkingEnd.TotalSeconds;

            if (_mediaReady)
            {
                if (Player.Position < _vm.WorkingStart || Player.Position >= _vm.WorkingEnd)
                {
                    SafeSeek(_vm.WorkingStart);
                    PausePlayback();
                }
            }

            DrawGuide();
            UpdateTimeLabel();
        }

        private void Vm_CurrentWordChanged(EditorWordItem? word)
        {
            if (word != null)
            {
                var container = WordsItemsControl.ItemContainerGenerator.ContainerFromItem(word) as FrameworkElement;
                container?.BringIntoView();
            }
        }

        private void Player_MediaOpened(object sender, RoutedEventArgs e)
        {
            _mediaReady = true;
            if (_vm == null) return;

            SafeSeek(_vm.WorkingStart);
            PausePlayback();

            SeekSlider.Minimum = _vm.WorkingStart.TotalSeconds;
            SeekSlider.Maximum = _vm.WorkingEnd.TotalSeconds;

            DrawGuide();
        }

        private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
        {
            _mediaReady = false;
            if (_vm != null)
            {
                _vm.PreviewError = "Preview is not available for this video format; editing still works.";
            }
            PlayPauseButton.IsEnabled = false;
            RestartButton.IsEnabled = false;
        }

        private void Timer_Tick(object? sender, EventArgs e)
        {
            if (_vm == null) return;

            var pos = Player.Position;

            if (pos >= _vm.WorkingEnd - TimeSpan.FromMilliseconds(30))
            {
                PausePlayback();
                SafeSeek(_vm.WorkingStart);
                return;
            }

            if ((DateTime.Now - _lastSeekCutTime).TotalMilliseconds >= 250)
            {
                foreach (var cut in _vm.PreviewCuts)
                {
                    if (pos >= cut.Start - TimeSpan.FromMilliseconds(20) && pos < cut.End)
                    {
                        var target = cut.End;
                        if (target >= _vm.WorkingEnd)
                        {
                            PausePlayback();
                            SafeSeek(_vm.WorkingStart);
                        }
                        else
                        {
                            SafeSeek(target);
                            _lastSeekCutTime = DateTime.Now;
                        }
                        break;
                    }
                }
            }

            _vm.UpdateCurrentWord(Player.Position);

            if (!SeekSlider.IsMouseCaptureWithin)
            {
                SeekSlider.Value = Player.Position.TotalSeconds;
            }

            UpdateTimeLabel();
        }

        private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            TogglePlayback();
        }

        private void RestartButton_Click(object sender, RoutedEventArgs e)
        {
            if (_vm == null) return;
            SafeSeek(_vm.WorkingStart);
            StartPlayback();
        }

        private void TogglePlayback()
        {
            if (_isPlaying)
            {
                PausePlayback();
            }
            else
            {
                if (_vm != null && (Player.Position < _vm.WorkingStart || Player.Position >= _vm.WorkingEnd))
                {
                    SafeSeek(_vm.WorkingStart);
                }
                StartPlayback();
            }
        }

        private void StartPlayback()
        {
            _isPlaying = true;
            PlayPauseButton.Content = "Pause";
            Player.Play();
            _timer.Start();
        }

        private void PausePlayback()
        {
            _isPlaying = false;
            PlayPauseButton.Content = "Play";
            Player.Pause();
            _timer.Stop();
            UpdateTimeLabel();
        }

        private void SafeSeek(TimeSpan position)
        {
            if (!_mediaReady) return;
            try
            {
                Player.Position = position;
            }
            catch (Exception ex)
            {
                if (_vm != null)
                {
                    _vm.PreviewError = $"Seek failed: {ex.Message}";
                }
            }
        }

        private void SeekSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            if (SeekSlider.IsMouseCaptureWithin)
            {
                SafeSeek(TimeSpan.FromSeconds(e.NewValue));
                UpdateTimeLabel();
                if (_vm != null)
                {
                    _vm.UpdateCurrentWord(Player.Position);
                }
            }
        }

        private void UpdateTimeLabel()
        {
            if (_vm == null) return;
            var posOffset = Player.Position - _vm.WorkingStart;
            var durOffset = _vm.WorkingEnd - _vm.WorkingStart;

            if (posOffset < TimeSpan.Zero) posOffset = TimeSpan.Zero;

            TimeLabel.Text = $"{posOffset.Minutes}:{posOffset.Seconds:D2} / {durOffset.Minutes}:{durOffset.Seconds:D2}";
        }

        private void PlayFromHere_Click(object sender, RoutedEventArgs e)
        {
            var menuItem = sender as MenuItem;
            var word = menuItem?.DataContext as EditorWordItem;

            if (word == null && menuItem?.Parent is ContextMenu cm && cm.PlacementTarget is FrameworkElement fe)
            {
                word = fe.DataContext as EditorWordItem;
            }

            if (word != null && !word.IsOutside)
            {
                SafeSeek(word.Start);
                StartPlayback();
            }
        }

        private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space)
            {
                TogglePlayback();
                e.Handled = true;
            }
        }

        private void PreviewGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DrawGuide();
        }

        private void DrawGuide()
        {
            GuideCanvas.Children.Clear();

            double gw = PreviewGrid.ActualWidth;
            double gh = PreviewGrid.ActualHeight;
            double vw = Player.NaturalVideoWidth;
            double vh = Player.NaturalVideoHeight;

            if (gw <= 0 || gh <= 0 || vw <= 0 || vh <= 0) return;

            double scale = Math.Min(gw / vw, gh / vh);
            double dw = vw * scale;
            double dh = vh * scale;
            double dx = (gw - dw) / 2;
            double dy = (gh - dh) / 2;

            double cropW = Math.Min(dw, dh * 9.0 / 16.0);
            double cropX = dx + (dw - cropW) / 2;

            var leftRect = new Rectangle
            {
                Width = cropX - dx,
                Height = dh,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(255 * 0.55), 0, 0, 0))
            };
            Canvas.SetLeft(leftRect, dx);
            Canvas.SetTop(leftRect, dy);

            var rightRect = new Rectangle
            {
                Width = dw - (cropX + cropW - dx),
                Height = dh,
                Fill = new SolidColorBrush(Color.FromArgb((byte)(255 * 0.55), 0, 0, 0))
            };
            Canvas.SetLeft(rightRect, cropX + cropW);
            Canvas.SetTop(rightRect, dy);

            var borderRect = new Rectangle
            {
                Width = cropW,
                Height = dh,
                Stroke = Brushes.White,
                StrokeThickness = 1
            };
            Canvas.SetLeft(borderRect, cropX);
            Canvas.SetTop(borderRect, dy);

            GuideCanvas.Children.Add(leftRect);
            GuideCanvas.Children.Add(rightRect);
            GuideCanvas.Children.Add(borderRect);
        }
    }
}
