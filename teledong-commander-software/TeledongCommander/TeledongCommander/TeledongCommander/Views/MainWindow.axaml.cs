using Avalonia.Controls;

namespace TeledongCommander.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Loaded += MainWindow_Loaded;
    }

    private void MainWindow_Loaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var screen = Screens.ScreenFromWindow(this);
        if (screen == null)
            return;

        if (Width > screen.WorkingArea.Width)
            Width = screen.WorkingArea.Width;
        if (Height > screen.WorkingArea.Height)
            Height = screen.WorkingArea.Height;
    }
}