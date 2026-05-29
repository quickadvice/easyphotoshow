using System.ComponentModel;
using System.Windows.Controls;
using EasyPhotoShow.App.ViewModels;

namespace EasyPhotoShow.App.Views;

public partial class DuplicateReviewView : UserControl
{
    private DuplicateReviewViewModel? _vm;

    public DuplicateReviewView()
    {
        InitializeComponent();
        // DataContext is set by the navigation host after construction. Hook on each
        // change so the scroll reset works even if the host re-binds (e.g. on back/forward).
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, System.Windows.DependencyPropertyChangedEventArgs e)
    {
        if (_vm is not null) _vm.PropertyChanged -= OnVmPropertyChanged;
        _vm = DataContext as DuplicateReviewViewModel;
        if (_vm is not null) _vm.PropertyChanged += OnVmPropertyChanged;
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Reset the scroll position whenever the page changes so the user always
        // lands at the top of the new page rather than partway down at the previous
        // page's scroll offset.
        if (e.PropertyName == nameof(DuplicateReviewViewModel.CurrentPage))
            GroupsScroll.ScrollToTop();
    }
}
