using System.Globalization;
using System.Windows.Controls;
using System.Windows.Input;
using EasyPhotoShow.App.ViewModels;

namespace EasyPhotoShow.App.Views;

public partial class SlideshowCreationView : UserControl
{
    private const double Step = 0.5;
    private const double Min = 1.0;
    private const double Max = 20.0;

    public SlideshowCreationView() { InitializeComponent(); }

    private void SecondsPerPhotoUpClick(object sender, System.Windows.RoutedEventArgs e) => Spin(+Step);
    private void SecondsPerPhotoDownClick(object sender, System.Windows.RoutedEventArgs e) => Spin(-Step);

    // Keyboard up/down arrows nudge by ±0.5 when the textbox has focus.
    private void SecondsPerPhotoBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Up) { Spin(+Step); e.Handled = true; }
        else if (e.Key == Key.Down) { Spin(-Step); e.Handled = true; }
        else if (e.Key == Key.Enter)
        {
            CommitTextBox();
            e.Handled = true;
        }
    }

    private void SecondsPerPhotoBox_LostFocus(object sender, System.Windows.RoutedEventArgs e) => CommitTextBox();

    // Parse the box, clamp, and assign back to the VM. Restores the displayed value
    // from the VM if the user typed something unparseable.
    private void CommitTextBox()
    {
        if (DataContext is not SlideshowCreationViewModel vm) return;
        if (!double.TryParse(SecondsPerPhotoBox.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out var parsed)
            && !double.TryParse(SecondsPerPhotoBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out parsed))
        {
            // Unparseable — snap displayed text back to the VM's current value.
            SecondsPerPhotoBox.Text = vm.SecondsPerPhoto.ToString("N1", CultureInfo.CurrentCulture);
            return;
        }
        vm.SecondsPerPhoto = Clamp(RoundToStep(parsed));
        SecondsPerPhotoBox.Text = vm.SecondsPerPhoto.ToString("N1", CultureInfo.CurrentCulture);
    }

    private void Spin(double delta)
    {
        if (DataContext is not SlideshowCreationViewModel vm) return;
        vm.SecondsPerPhoto = Clamp(RoundToStep(vm.SecondsPerPhoto + delta));
        // Reflect immediately in the textbox in case the binding hasn't refreshed yet
        // (UpdateSourceTrigger=LostFocus on the binding means typing changes wouldn't
        // push back until LostFocus, but writing the VM here drives the inverse direction
        // so the text always shows the canonical clamped value).
        SecondsPerPhotoBox.Text = vm.SecondsPerPhoto.ToString("N1", CultureInfo.CurrentCulture);
    }

    private static double Clamp(double v) => System.Math.Min(Max, System.Math.Max(Min, v));

    // Snap to the nearest 0.5 step so typed-in values like "3.7" become "3.5" on commit.
    private static double RoundToStep(double v) => System.Math.Round(v / Step) * Step;
}
