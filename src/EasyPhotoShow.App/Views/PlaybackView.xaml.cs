using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using EasyPhotoShow.App.ViewModels;

namespace EasyPhotoShow.App.Views;

public partial class PlaybackView : UserControl
{
    private readonly DispatcherTimer _timer;
    private bool _isDragging;
    private bool _isPlaying;
    private WindowState _previousWindowState;
    private WindowStyle _previousWindowStyle;
    private ResizeMode _previousResizeMode;
    private bool _isFullscreen;

    public PlaybackView()
    {
        InitializeComponent();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _timer.Tick += (_, _) => UpdatePosition();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlaybackViewModel vm && System.IO.File.Exists(vm.VideoPath))
        {
            Media.Source = new Uri(vm.VideoPath);
            Media.Volume = VolumeSlider.Value;
            Media.Play();
            _isPlaying = true;
            PlayPauseBtn.Content = "Pause";
            _timer.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _timer.Stop();
        Media.Stop();
        Media.Source = null;
        if (_isFullscreen) ExitFullscreen();
    }

    private void OnMediaOpened(object sender, RoutedEventArgs e)
    {
        if (Media.NaturalDuration.HasTimeSpan)
        {
            Position.Maximum = Media.NaturalDuration.TimeSpan.TotalSeconds;
        }
    }

    private void OnMediaEnded(object sender, RoutedEventArgs e)
    {
        _isPlaying = false;
        PlayPauseBtn.Content = "Play";
    }

    private void OnPlayPause(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Media.Pause();
            _isPlaying = false;
            PlayPauseBtn.Content = "Play";
        }
        else
        {
            Media.Play();
            _isPlaying = true;
            PlayPauseBtn.Content = "Pause";
        }
    }

    private void OnReplay(object sender, RoutedEventArgs e)
    {
        Media.Position = TimeSpan.Zero;
        Media.Play();
        _isPlaying = true;
        PlayPauseBtn.Content = "Pause";
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        Media.Volume = e.NewValue;
    }

    private void OnSliderDragStarted(object sender, DragStartedEventArgs e) => _isDragging = true;
    private void OnSliderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        _isDragging = false;
        Media.Position = TimeSpan.FromSeconds(Position.Value);
    }
    private void OnSliderClick(object sender, MouseButtonEventArgs e)
    {
        Media.Position = TimeSpan.FromSeconds(Position.Value);
    }

    private void UpdatePosition()
    {
        if (_isDragging) return;
        var pos = Media.Position;
        Position.Value = pos.TotalSeconds;
        var total = Media.NaturalDuration.HasTimeSpan ? Media.NaturalDuration.TimeSpan : TimeSpan.Zero;
        TimeText.Text = $"{Format(pos)} / {Format(total)}";
    }

    private static string Format(TimeSpan t) => t.TotalHours >= 1
        ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
        : $"{t.Minutes}:{t.Seconds:D2}";

    private void OnFullscreen(object sender, RoutedEventArgs e)
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        if (_isFullscreen) ExitFullscreen();
        else EnterFullscreen(window);
    }

    private void EnterFullscreen(Window window)
    {
        _previousWindowState = window.WindowState;
        _previousWindowStyle = window.WindowStyle;
        _previousResizeMode = window.ResizeMode;
        window.WindowStyle = WindowStyle.None;
        window.ResizeMode = ResizeMode.NoResize;
        window.WindowState = WindowState.Maximized;
        FullscreenBtn.Content = "Windowed";
        _isFullscreen = true;
    }

    private void ExitFullscreen()
    {
        var window = Window.GetWindow(this);
        if (window is null) return;
        window.WindowState = _previousWindowState;
        window.WindowStyle = _previousWindowStyle;
        window.ResizeMode = _previousResizeMode;
        FullscreenBtn.Content = "Fullscreen";
        _isFullscreen = false;
    }

    private void OnClose(object sender, RoutedEventArgs e)
    {
        if (DataContext is PlaybackViewModel vm)
            vm.BackToDoneCommand.Execute(null);
    }
}
