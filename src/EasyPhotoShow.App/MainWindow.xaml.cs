using System.ComponentModel;
using System.Windows;
using EasyPhotoShow.App.ViewModels;

namespace EasyPhotoShow.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        App.Navigation.CurrentChanged += OnCurrentChanged;
        ContentHost.Content = App.Navigation.Current;
    }

    private void OnCurrentChanged(object? sender, EventArgs e)
    {
        ContentHost.Content = App.Navigation.Current;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (App.Navigation.Current is RenderingViewModel rendering && rendering.IsRendering)
        {
            var result = MessageBox.Show(
                "Your slideshow is still being created. Closing EasyPhotoShow now will stop the slideshow before it is complete.",
                "EasyPhotoShow",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);

            if (result == MessageBoxResult.Cancel)
            {
                e.Cancel = true;
                return;
            }
            rendering.CancelCommand.Execute(null);
        }
        base.OnClosing(e);
    }
}
