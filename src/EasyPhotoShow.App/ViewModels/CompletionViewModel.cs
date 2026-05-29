using System.Diagnostics;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace EasyPhotoShow.App.ViewModels;

public partial class CompletionViewModel : ObservableObject
{
    public string OutputPath { get; }
    public string Headline { get; } = "Your slideshow is complete.";
    public string Subhead { get; } = "Would you like to view it now?";

    public CompletionViewModel(string outputPath)
    {
        OutputPath = outputPath;
    }

    [RelayCommand]
    private void ViewSlideshow()
    {
        App.Navigation.NavigateTo(new PlaybackViewModel(OutputPath));
    }

    [RelayCommand]
    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{OutputPath}\"",
                UseShellExecute = true
            });
        }
        catch { }
    }

    [RelayCommand]
    private void Done()
    {
        App.Navigation.NavigateTo(new MainScreenViewModel());
    }
}
