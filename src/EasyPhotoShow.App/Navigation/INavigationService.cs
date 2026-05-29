using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyPhotoShow.App.Navigation;

public interface INavigationService
{
    ObservableObject? Current { get; }
    void NavigateTo(ObservableObject viewModel);
    event EventHandler? CurrentChanged;
}
