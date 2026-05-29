using CommunityToolkit.Mvvm.ComponentModel;

namespace EasyPhotoShow.App.Navigation;

public sealed class NavigationService : INavigationService
{
    private ObservableObject? _current;

    public ObservableObject? Current
    {
        get => _current;
        private set
        {
            _current = value;
            CurrentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public event EventHandler? CurrentChanged;

    public void NavigateTo(ObservableObject viewModel)
    {
        Current = viewModel;
    }
}
