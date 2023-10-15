using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using System.IO;
using System.Linq;
using TeledongCommander.ViewModels;

namespace TeledongCommander.Views
{
    public partial class MainView : UserControl
    {
        public MainView()
        {
            InitializeComponent();

            inputPositionChart.YAxes /*= outputPositionChart.YAxes*/ = new Axis[]
            {
                new Axis
                {
                    Name = null,
                    LabelsPaint = null,
                    ShowSeparatorLines = false,
                    MinLimit = 0,
                    MaxLimit = 1,
                }
            };

            Unloaded += MainView_Unloaded;

            PointerMoved += MainView_PointerMoved;

        }

        private void MainView_PointerMoved(object? sender, Avalonia.Input.PointerEventArgs e)
        {
            (DataContext as MainViewModel)?.UpdatePointerMovement(e.GetCurrentPoint(this).Position.Y);
        }

        private void MainView_Unloaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            (DataContext as MainViewModel)?.SaveAndFree();
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

            if (DataContext as MainViewModel is MainViewModel viewModel)
            {
                var filePath = path.Path.LocalPath;
                if (Path.GetExtension(filePath) != ".funscript")
                    filePath = Path.ChangeExtension(filePath, ".funscript");
                viewModel.FunscriptOutputPath = filePath;
            }
        }

    }
}