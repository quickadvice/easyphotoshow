using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;

namespace EasyPhotoShow.App.ViewModels;

public partial class MainScreenViewModel : ObservableObject
{
    public ObservableCollection<string> Folders { get; } = new();

    [ObservableProperty]
    private bool hasFolders;

    public MainScreenViewModel()
    {
        foreach (var f in App.Session.SourceFolders)
            Folders.Add(f);
        HasFolders = Folders.Count > 0;
        Folders.CollectionChanged += (_, _) =>
        {
            App.Session.SourceFolders.Clear();
            App.Session.SourceFolders.AddRange(Folders);
            HasFolders = Folders.Count > 0;
        };
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dialog = new OpenFolderDialog
        {
            Title = "Choose a folder of photos"
        };
        if (dialog.ShowDialog() == true)
        {
            var path = dialog.FolderName;
            if (!Folders.Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase)))
                Folders.Add(path);
        }
    }

    [RelayCommand]
    private void RemoveFolder(string folder)
    {
        Folders.Remove(folder);
    }

    [RelayCommand(CanExecute = nameof(HasFolders))]
    private void ReviewDuplicates()
    {
        App.Navigation.NavigateTo(new ScanningViewModel(reviewDuplicatesAfter: true));
    }

    [RelayCommand(CanExecute = nameof(HasFolders))]
    private void UseAllPhotos()
    {
        App.Navigation.NavigateTo(new ScanningViewModel(reviewDuplicatesAfter: false));
    }

    partial void OnHasFoldersChanged(bool value)
    {
        ReviewDuplicatesCommand.NotifyCanExecuteChanged();
        UseAllPhotosCommand.NotifyCanExecuteChanged();
    }
}
