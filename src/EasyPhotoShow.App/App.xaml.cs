using System.Windows;
using EasyPhotoShow.App.Navigation;
using EasyPhotoShow.App.Session;
using EasyPhotoShow.App.ViewModels;

namespace EasyPhotoShow.App;

public partial class App : Application
{
    public static INavigationService Navigation { get; private set; } = null!;
    public static SlideshowSession Session { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Navigation = new NavigationService();
        Session = new SlideshowSession();
        Navigation.NavigateTo(new MainScreenViewModel());
    }
}
