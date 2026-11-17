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

    private void BrowseRecordingsFolder_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SettingsViewModel settings)
        {
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Folder for recordings",
            InitialDirectory = System.IO.Directory.Exists(settings.RecordingsFolder) ? settings.RecordingsFolder : AppContext.BaseDirectory,
        };

        if (dialog.ShowDialog(this) == true)
        {
            settings.RecordingsFolder = dialog.FolderName;
        }
    }

    private void OkButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is SettingsViewModel { IsValid: true })
        {
            DialogResult = true;
        }
    }
}
