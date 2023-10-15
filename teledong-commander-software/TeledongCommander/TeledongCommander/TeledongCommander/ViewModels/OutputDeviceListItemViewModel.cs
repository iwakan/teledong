using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeledongCommander.ViewModels;

public partial class OutputDeviceListItemViewModel : ViewModelBase
{
    [ObservableProperty]
    private string title = "ttitle";

    [ObservableProperty]
    private string? secondaryTitle = "sec. title";

    [ObservableProperty]
    private string statusLabelSymbol = "☐";//☐🗹

    [ObservableProperty]
    private Brush statusLabelBrush = new SolidColorBrush(Colors.LightGray);

    [ObservableProperty]
    private bool isConnected = false;

    public OutputDevice OutputDevice { get; private set; }

    /*public OutputDeviceListItemViewModel(OutputDevice outputDevice)
    {
        OutputDevice = outputDevice;
        OutputDevice.StatusChanged += OutputDevice_StatusChanged;

        if (OutputDevice is HandyOnlineApi handyApi)
        {
            Title = "The Handy Connection Key";
            SecondaryTitle = handyApi.ConnectionKey;
        }
        else if (OutputDevice is FunscriptRecorder funscriptRecorder)
        {
            Title = "Funscript recorder";
            SecondaryTitle = AbbreviatePath(funscriptRecorder.OutputPath);
        }
        else if (OutputDevice is ButtplugApi buttplugApi)
        {
            Title = "Funscript recorder";
            SecondaryTitle = null;
        }
    }*/

    ~OutputDeviceListItemViewModel()
    {
        OutputDevice.StatusChanged -= OutputDevice_StatusChanged;
    }

    private void OutputDevice_StatusChanged(object? sender, EventArgs e)
    {
        if (OutputDevice is HandyOnlineApi handyApi)
        {
            SecondaryTitle = handyApi.ConnectionKey;
        }
        else if (OutputDevice is FunscriptRecorder funscriptRecorder)
        {
            SecondaryTitle = AbbreviatePath(funscriptRecorder.OutputPath);
        }
        else if (OutputDevice is ButtplugApi buttplugApi)
        {
            SecondaryTitle = buttplugApi.StatusText;
        }

        if (OutputDevice.HasError)
        {
        }
        else if (OutputDevice.IsConnected)
        {
            StatusLabelBrush = new SolidColorBrush(Colors.MediumSeaGreen);
            StatusLabelSymbol = "🗹";
        }
        else
        {
            StatusLabelBrush = new SolidColorBrush(Colors.LightGray);
            StatusLabelSymbol = "☐";
        }
    }

    private string AbbreviatePath(string path)
    {
        return (path.Length > 26 ? "..." : "") + path.Substring(Math.Max(0, path.Length - 26));
    }
}
