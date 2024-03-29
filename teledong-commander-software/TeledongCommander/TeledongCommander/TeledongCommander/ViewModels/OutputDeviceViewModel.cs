﻿using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeledongCommander.ViewModels;

public partial class OutputDeviceViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _title = "title";

    [ObservableProperty]
    private string? _secondaryTitle = "sec. title";

    [ObservableProperty]
    private string _statusLabelSymbol = "☐";//☐🗹

    [ObservableProperty]
    private Brush _statusLabelBrush = new SolidColorBrush(Colors.LightGray);

    [ObservableProperty]
    private bool _isStarted = false;

    //[ObservableProperty]
    //private double writeInterval = 50;

    //[ObservableProperty]
    //private double writeCommandDuration = 250;

    [ObservableProperty]
    private double _filterStrength = 0.0;

    [ObservableProperty]
    private double _filterTimeMilliseconds = 0;

    [ObservableProperty]
    private bool _peakMotionMode = true;

    public OutputDevice OutputDevice { get; private set; }
    public virtual string SettingsId { get; } = "";

    public event EventHandler? Removed;

    protected OutputDeviceViewModel(OutputDevice outputDevice)
    {
        OutputDevice = outputDevice;
        OutputDevice.StatusChanged += OutputDevice_StatusChanged;
        OutputDevice.Processor.FilterStrength = FilterStrength;
        OutputDevice.Processor.FilterTime = TimeSpan.FromMilliseconds(FilterTimeMilliseconds);
        OutputDevice.Processor.PeakMotionMode = PeakMotionMode;
    }

    [RelayCommand]
    protected virtual void Start() 
    {
        OutputDevice.Start();
    }

    [RelayCommand]
    protected virtual void Stop() 
    {
        OutputDevice.Stop();
    }

    [RelayCommand]
    protected virtual void Remove()
    {
        Removed?.Invoke(this, EventArgs.Empty);
    }

    partial void OnFilterStrengthChanged(double value)
    {
        OutputDevice.Processor.FilterStrength = value;
    }

    partial void OnFilterTimeMillisecondsChanged(double value)
    {
        OutputDevice.Processor.FilterTime = TimeSpan.FromMilliseconds(value);
    }

    partial void OnPeakMotionModeChanged(bool value)
    {
        OutputDevice.Processor.PeakMotionMode = value;
    }

    protected virtual void OutputDevice_StatusChanged(object? sender, EventArgs e)
    {
        IsStarted = OutputDevice.IsStarted;

        if (OutputDevice.HasError)
        {
            StatusLabelBrush = new SolidColorBrush(Colors.
                Orange);
            StatusLabelSymbol = "⚠";
        }
        else if (OutputDevice.IsStarted)
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

    ~OutputDeviceViewModel()
    {
        OutputDevice.StatusChanged -= OutputDevice_StatusChanged;
    }
}
