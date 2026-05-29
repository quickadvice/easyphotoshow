using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EasyPhotoShow.App.ViewModels;

public partial class PlaybackViewModel : ObservableObject
{
    public string VideoPath { get; }

    public PlaybackViewModel(string videoPath)
    {
        VideoPath = videoPath;
    }

    [RelayCommand]
    private void BackToDone()
    {
        App.Navigation.NavigateTo(new CompletionViewModel(VideoPath));
    }
}
