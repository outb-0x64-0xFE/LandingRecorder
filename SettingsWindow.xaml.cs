using System.Windows;
using LandingRecorder.ViewModels;

namespace LandingRecorder;

public partial class SettingsWindow : Window
{
    public SettingsWindow(SettingsViewModel viewModel)
    {
        InitializeComponent();
        DataContext = viewModel;
    }

    private void PaletteColor_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: string hex } && ColorPopup.DataContext is PlotLineEditorViewModel line)
        {
            line.Color = hex;
        }
        ColorPopup.IsOpen = false;
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { IsValid: true })
        {
            DialogResult = true;
        }
    }
}
