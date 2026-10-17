using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml;
using SteamForge.App.ViewModels;

namespace SteamForge.App;

public partial class MainWindow : Window
{
    public MainWindow() => InitializeComponent();

    private void InitializeComponent() => AvaloniaXamlLoader.Load(this);

    private MainViewModel? Vm => DataContext as MainViewModel;

    // Stop first: the run loop is async and would otherwise outlive the window.
    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm?.StopCommand is { } stop && stop.CanExecute(null))
            stop.Execute(null);
        Close();
    }

    private async void BrowseCombo_Click(object? sender, RoutedEventArgs e)
    {
        if (Vm is null) return;

        try
        {
            var files = await StorageProvider.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
            {
                Title = "Select combo file",
                AllowMultiple = false,
                FileTypeFilter = new[]
                {
                    new Avalonia.Platform.Storage.FilePickerFileType("Text files")
                    {
                        Patterns = new[] { "*.txt", "*.csv", "*.combos" },
                    },
                    new Avalonia.Platform.Storage.FilePickerFileType("All files")
                    {
                        Patterns = new[] { "*.*" },
                    },
                },
            });

            if (files.Count == 0) return;

            // IStorageFile.Path is a Uri; for a genuinely local selection it is a file:// URI.
            var uri = files[0].Path;
            if (uri is not null && uri.IsFile)
                Vm.ComboPath = uri.LocalPath;
        }
        catch
        {
            // Picker cancelled or unavailable — leave the typed path alone.
        }
    }
}
