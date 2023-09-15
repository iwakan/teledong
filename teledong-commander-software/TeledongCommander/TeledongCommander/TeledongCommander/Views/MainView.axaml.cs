using Avalonia.Controls;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using TeledongCommander.ViewModels;

namespace TeledongCommander.Views
{
    public partial class MainView : UserControl
    {
        public MainView()
        {
            InitializeComponent();

            inputPositionChart.YAxes = outputPositionChart.YAxes = new Axis[]
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
    }
}