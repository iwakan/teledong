using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using System.IO;
using TeledongCommander.ViewModels;

namespace TeledongCommander;

public partial class FunscriptRecorderSettingsView : UserControl
{
    public FunscriptRecorderSettingsView()
    {
        InitializeComponent();
    }

    private async void ChangeFunscriptPathButton_Clicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel == null)
            return;
        var path = await topLevel!.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Choose funscript file output path",
        });
        if (path == null)
            return;

        if (DataContext as FunscriptRecorderViewModel is FunscriptRecorderViewModel viewModel)
        {
            var filePath = path.Path.LocalPath;
            if (Path.GetExtension(filePath) != ".funscript")
                filePath = Path.ChangeExtension(filePath, ".funscript");
            viewModel.OutputPath = filePath;
        }
    }
}