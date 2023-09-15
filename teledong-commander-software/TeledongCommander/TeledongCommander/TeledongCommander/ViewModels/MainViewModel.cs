using Avalonia.Controls.Converters;
using Avalonia.Input;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore;
using ReactiveUI;
using System;
using System.ComponentModel;
using System.Reactive;
using System.Runtime.CompilerServices;
using Teledong;
using Avalonia.Media;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Avalonia;
using LiveChartsCore.Defaults;
using Avalonia.Threading;
using System.Runtime.InteropServices;
using System.Threading;
using System.Diagnostics;
using System.Timers;
using System.IO;
using System.Xml;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Avalonia.Controls;

namespace TeledongCommander.ViewModels;

// View model for the main app window
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string outputDeviceStatusText = "";

    [ObservableProperty]
    private string inputDeviceStatusText = "";

    [ObservableProperty]
    private string theHandyConnectionKey = "";

    [ObservableProperty]
    private bool peakMotionMode = false;

    [ObservableProperty]
    private bool teledongSunlightMode = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebugMode))]
    private bool advancedSettingsAreOpen = false;

    public bool DebugMode => AdvancedSettingsAreOpen;

    [ObservableProperty]
    private int teledongFirmwareVersion;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsNone))]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsHandyKey))]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsButtplugIo))]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsIntiface))]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsFunscript))]
    private OutputDevice outputDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsTeledong))]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsMouse))]
    private InputDevice inputDevice;

    public bool OutputDeviceIsNone => OutputDevice == OutputDevice.None;
    public bool OutputDeviceIsHandyKey => OutputDevice == OutputDevice.HandyHttpApi;
    public bool OutputDeviceIsButtplugIo => OutputDevice == OutputDevice.ButtplugIo;
    public bool OutputDeviceIsIntiface => OutputDevice == OutputDevice.Intiface;
    public bool OutputDeviceIsFunscript => OutputDevice == OutputDevice.Funscript;

    public bool InputDeviceIsTeledong => InputDevice == InputDevice.Teledong;
    public bool InputDeviceIsMouse => InputDevice == InputDevice.Mouse;

    [ObservableProperty]
    private double readInterval = 20;

    [ObservableProperty]
    private double writeInterval = 80;

    [ObservableProperty]
    private double writeCommandDuration = 250;

    [ObservableProperty]
    private string sensorValuesDebugString = "";

    [ObservableProperty]
    private ISeries[] inputPositionSeries = new ISeries[]
    {
        new LineSeries<ObservablePoint> 
        {
            Values = new List<ObservablePoint>(),
            Fill = null,
            Stroke = new SolidColorPaint(SKColors.White) {StrokeThickness = 2},
            GeometrySize = 2,
            GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 2},
            LineSmoothness = 0,
        }
    };

    [ObservableProperty]
    private ISeries[] outputPositionSeries = new ISeries[]
    {
        new LineSeries<ObservablePoint>
        {
            Values = new List<ObservablePoint>(),
            Fill = null,
            Stroke = new SolidColorPaint(SKColors.White) {StrokeThickness = 2},
            GeometrySize = 2,
            GeometryStroke = new SolidColorPaint(SKColors.White) { StrokeThickness = 2},
            LineSmoothness = 0,
        }
    };

    [ObservableProperty]
    private Axis[] positionChartXAxes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionChartXAxes))]
    private double? positionChartMinX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionChartXAxes))]
    private double? positionChartMaxX;

    [ObservableProperty]
    private bool canClickConnectInputDeviceButton;

    [ObservableProperty]
    private bool canClickConnectOutputDeviceButton;

    TeledongManager? teledongModel;
    ButtplugApi? buttplugIoModel;
    HandyOnlineApi? handyOnlineApiModel;

    System.Timers.Timer sensorReadTimer = new();
    System.Timers.Timer writeTimer = new();
    DispatcherTimer uiUpdateTimer = new();

    const string settingsFolderName = "TeledongCommander";

    double position = 0;
    double previousPosition = 0.9;
    double previousPointerYPosition = 0;

    StrokeDirection currentDirection = StrokeDirection.None;
    DateTime lastWriteTime = DateTime.Now;

    int writeCommandsOngoing = 0;
    bool isCalibrating = false;
    DateTime chartStartTime = DateTime.Now - TimeSpan.FromSeconds(10);

    public MainViewModel()
    {
        positionChartXAxes = new Axis[]
        {
            new Axis
            {
                Name = null,
                LabelsPaint = null,
                ShowSeparatorLines = false,
                MinLimit = 0,
                MaxLimit = 10,
            }
        };

        sensorReadTimer.Interval = 20;
        sensorReadTimer.Elapsed += SensorReadTimer_Tick;

        writeTimer.Interval = 80;
        writeTimer.Elapsed += WriteTimer_Tick;

        uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
        uiUpdateTimer.Tick += UiUpdateTimer_Tick;

        LoadSettings();

        writeTimer.Start();
        sensorReadTimer.Start();
        uiUpdateTimer.Start();
    }

    private void UiUpdateTimer_Tick(object? sender, EventArgs e)
    {
        // Todo handle this stuff immediately upon change using bindings or whatever, rather than in this timer
        UpdateUiStatus();
    }

    private void UpdateUiStatus()
    {
        if (InputDeviceIsTeledong)
        {
            if (teledongModel == null || teledongModel.State == TeledongState.NotConnected)
                InputDeviceStatusText = "Not connected";
            else if (teledongModel.State == TeledongState.Calibrating)
                InputDeviceStatusText = "Teledong connected, calibrating...";
            else if (teledongModel.State == TeledongState.Ok)
                InputDeviceStatusText = "Teledong connected, OK";
            else if (teledongModel.State == TeledongState.Error)
                InputDeviceStatusText = "ERROR";
        }
        else if (InputDeviceIsMouse)
        {
            InputDeviceStatusText = "Mouse input enabled";
        }
        else
            InputDeviceStatusText = "No input device selected";

        if (OutputDeviceIsButtplugIo)
            OutputDeviceStatusText = buttplugIoModel?.StatusText ?? "Not connected";
        else if (OutputDeviceIsHandyKey)
            OutputDeviceStatusText = handyOnlineApiModel?.StatusText ?? "Not connected";
        else if (OutputDeviceIsIntiface)
            OutputDeviceStatusText = "Not yet supported";
        else if (OutputDeviceIsFunscript)
            OutputDeviceStatusText = "Not yet supported";
        else
            OutputDeviceStatusText = "No output device selected";

        CanClickConnectInputDeviceButton = InputDeviceIsTeledong;
        CanClickConnectOutputDeviceButton = !OutputDeviceIsNone && !OutputDeviceIsFunscript;
    }

    /*private void ChartUpdateTimer_Tick(object? sender, EventArgs e)
    {
        throw new NotImplementedException();
    }*/

    [RelayCommand]
    private void SetOutputDevice(string outputDeviceId)
    {
        OutputDevice = (OutputDevice)int.Parse(outputDeviceId);

        if (OutputDeviceIsHandyKey)
            PeakMotionMode = true;
        else
            PeakMotionMode = false;
    }

    [RelayCommand]
    private void SetInputDevice(string inputDeviceId)
    {
        InputDevice = (InputDevice)int.Parse(inputDeviceId);
    }

    private void WriteTimer_Tick(object? sender, ElapsedEventArgs e)
    {
        bool shouldSendPosition = false;
        double positionToSend = 1.0;

        if (!PeakMotionMode)
        {
            // Direct forwarding, write a new position to the output device if the input position has changed
            var difference = Math.Abs(position - previousPosition);
            if (difference > 0.05)
            {
                shouldSendPosition = true;
                positionToSend = position;

                previousPosition = position;
            }
        }
        else
        {
            // Peak motion write mode. Only writes a position after the motion on the Teledong has stopped/reversed.
            // Should mean that positions are usually only written twice per stroke, at the max/min amplitude peaks. 
            // This mode can be used if the device API favors more rare updates due to latency etc, such as The Handy http API.

            var positionDelta = position - previousPosition;

            if (currentDirection == StrokeDirection.Up)
            {
                if (position <= previousPosition || DateTime.Now - lastWriteTime > TimeSpan.FromSeconds(1))
                {
                    Debug.WriteLine("Strokedir.: Up");

                    shouldSendPosition = true;

                    if (position <= previousPosition)
                        currentDirection = StrokeDirection.None;
                }
            }
            else if (currentDirection == StrokeDirection.Down)
            {
                if (position >= previousPosition || DateTime.Now - lastWriteTime > TimeSpan.FromSeconds(1))
                {
                    Debug.WriteLine("Strokedir.: Down");

                    shouldSendPosition = true;
                    
                    if (position >= previousPosition)
                        currentDirection = StrokeDirection.None;
                }
            }

            if (shouldSendPosition)
            {
                if (DateTime.Now - lastWriteTime < TimeSpan.FromMilliseconds(100) && positionDelta < 0.1)
                    shouldSendPosition = false; // Debouncing

                positionToSend = previousPosition;
                lastWriteTime = DateTime.Now;
            }

            if (Math.Abs(positionDelta) > 0.05)
            {
                currentDirection = positionDelta > 0 ? StrokeDirection.Up : StrokeDirection.Down;
                previousPosition = position;
            }

            if (currentDirection != StrokeDirection.None)
                previousPosition = position;
        }


        if (shouldSendPosition)
        {
            if (writeCommandsOngoing > 0)
                Debug.WriteLine("Warning: Write congestion: " + writeCommandsOngoing);

            writeCommandsOngoing++;

            if (OutputDeviceIsButtplugIo && (buttplugIoModel?.IsConnected ?? false))
            {
                buttplugIoModel?.SendPosition(Math.Clamp(positionToSend, 0, 1), TimeSpan.FromMilliseconds(WriteCommandDuration));
                SendPointToOutputPositionChart(positionToSend);
            }
            else if (OutputDeviceIsHandyKey && (handyOnlineApiModel?.IsConnected ?? false))
            {
                handyOnlineApiModel?.SendPosition(Math.Clamp(positionToSend, 0, 1), TimeSpan.FromMilliseconds(WriteCommandDuration));
                SendPointToOutputPositionChart(positionToSend);
            }

            writeCommandsOngoing--;
        }
    }

    private void SensorReadTimer_Tick(object? sender, ElapsedEventArgs e)
    {
        if (isCalibrating)
            return;

        try
        {
            if (InputDeviceIsTeledong && teledongModel != null && (teledongModel?.State != TeledongState.NotConnected))
            {
                position = teledongModel?.GetPosition() ?? 1.0;
                SendPointToInputPositionChart(position);

                if (DebugMode)
                {
                    try
                    {
                        string debugString = "";
                        
                        var rawValues = teledongModel!.GetRawSensorValues(false).ToList();
                        var calibratedValues = teledongModel!.GetRawSensorValues(true).ToList();

                        if (rawValues != null && calibratedValues != null)
                        {
                            for (int i = 0; i < rawValues.Count(); i++)
                            {
                                debugString += $"{((int)rawValues[i]).ToString("X2")} ";
                            }
                            debugString += "\n";
                            /*for (int i = 0; i < rawValues.Count(); i++)
                            {
                                debugString += $"{calibratedValues[i].ToString("X2")},";
                            }
                            debugString += "\n";*/
                        }

                        SensorValuesDebugString = debugString;
                    }
                    catch (Exception ex) { }

                }
            }
            else if (InputDeviceIsMouse)
            {
                SendPointToInputPositionChart(position);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Failed to read sensor: " + ex.Message);
        }
    }

    private void SendPointToInputPositionChart(double newPosition)
    {
        var inputPositionSeriesValues = InputPositionSeries[0].Values!.Cast<ObservablePoint>().ToList();
        var chartNowTime = (DateTime.Now - chartStartTime);

        inputPositionSeriesValues.Add(new ObservablePoint(chartNowTime.TotalSeconds, newPosition));
        var firstPoint = inputPositionSeriesValues.FirstOrDefault();
        while (firstPoint != null && firstPoint.X < (PositionChartMinX - 5))
        {
            inputPositionSeriesValues.RemoveAt(0);
            firstPoint = inputPositionSeriesValues.FirstOrDefault();
        }

        InputPositionSeries[0].Values = inputPositionSeriesValues;

        PositionChartMinX = (chartNowTime - TimeSpan.FromSeconds(5)).TotalSeconds;
        PositionChartMaxX = chartNowTime.TotalSeconds;
    }

    private void SendPointToOutputPositionChart(double newPosition)
    {
        var outputPositionSeriesValues = OutputPositionSeries[0].Values!.Cast<ObservablePoint>().ToList();
        var chartNowTime = (DateTime.Now - chartStartTime);

        outputPositionSeriesValues.Add(new ObservablePoint(chartNowTime.TotalSeconds, newPosition));
        var firstPoint = outputPositionSeriesValues.FirstOrDefault();
        while (firstPoint != null && firstPoint.X < (PositionChartMinX - 5))
        {
            outputPositionSeriesValues.RemoveAt(0);
            firstPoint = outputPositionSeriesValues.FirstOrDefault();
        }

        OutputPositionSeries[0].Values = outputPositionSeriesValues;

        PositionChartMinX = (chartNowTime - TimeSpan.FromSeconds(5)).TotalSeconds;
        PositionChartMaxX = chartNowTime.TotalSeconds;
    }

    [RelayCommand]
    private void ToggleAdvancedSettings()
    {
        AdvancedSettingsAreOpen = !AdvancedSettingsAreOpen;
    }

    [RelayCommand]
    private void ConnectInputDevice()
    {
        if (InputDeviceIsTeledong)
        {
            teledongModel ??= new();

            if (teledongModel.Connect())
            {
                try
                {
                    Debug.WriteLine("Found Teledong. Firmware version: " + teledongModel.GetFirmwareVersion());
                }
                catch (Exception ex) { }

                teledongModel.LoadCalibration();

                if (TeledongSunlightMode != teledongModel.SunlightMode)
                    TeledongSunlightMode = teledongModel.SunlightMode;

                TeledongFirmwareVersion = teledongModel.GetFirmwareVersion();
            }
            else
                Debug.WriteLine("Couldn't connect to Teledong");
        }
    }

    [RelayCommand]
    private async Task ConnectOutputDevice()
    {
        DisconnectOutputDevice();

        if (OutputDeviceIsButtplugIo)
        {
            buttplugIoModel ??= new();
        }
        else if (OutputDeviceIsHandyKey)
        {
            handyOnlineApiModel ??= new();
            handyOnlineApiModel.ConnectionKey = TheHandyConnectionKey;
            await handyOnlineApiModel.SetMode();
        }
    }

    [RelayCommand]
    private void TeledongBootToBootloader()
    {
        if (InputDeviceIsTeledong)
        {
            try
            {
                teledongModel?.SendNonQueryCommand(TeledongCommands.EnterBootloader);
            }
            catch (Exception ex) { }
        }
    }

    partial void OnPositionChartMinXChanged(double? value)
    {
        PositionChartXAxes[0].MinLimit = value;
    }

    partial void OnPositionChartMaxXChanged(double? value)
    {
        PositionChartXAxes[0].MaxLimit = value;
    }

    partial void OnInputDeviceChanged(InputDevice value)
    {
        UpdateUiStatus();
    }

    partial void OnOutputDeviceChanged(OutputDevice value)
    {
        UpdateUiStatus();
    }

    partial void OnReadIntervalChanged(double value)
    {
        sensorReadTimer.Interval = value;
    }

    partial void OnWriteIntervalChanged(double value)
    {
        writeTimer.Interval = value;
    }

    partial void OnTeledongSunlightModeChanged(bool value)
    {
        teledongModel?.SetSunlightMode(value);
    }

    private void DisconnectInputDevice()
    {
        teledongModel?.Disconnect();
    }

    [RelayCommand]
    private async Task CalibrateTeledong()
    {
        if (teledongModel == null || teledongModel.State == TeledongState.NotConnected || isCalibrating)
            return;

        isCalibrating = true;
        try
        {
            await (teledongModel?.CalibrateAsync() ?? Task.CompletedTask);

            if (TeledongSunlightMode != teledongModel.SunlightMode)
                TeledongSunlightMode = teledongModel.SunlightMode;
        }
        catch (Exception ex)
        {
            Debug.Write("Failed to calibrate teledong: " + ex.Message);
        }
        isCalibrating = false;
    }

    private void DisconnectOutputDevice()
    {
        buttplugIoModel?.Disconnect();
        handyOnlineApiModel?.Disconnect();
    }

    public void SaveAndFree()
    {
        sensorReadTimer.Stop();
        writeTimer.Stop();
        Thread.Sleep(200);

        DisconnectInputDevice();
        DisconnectOutputDevice();
        SaveSettings();
    }

    private void LoadSettings()
    {
        var settingsFilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), settingsFolderName, "settings.xml");
        if (File.Exists(settingsFilePath))
        {
            try
            {
                var settings = new XmlDocument();
                settings.Load(settingsFilePath);
                foreach (XmlElement node in settings.DocumentElement!.ChildNodes)
                {
                    if (node.Name == "InputSettings")
                    {
                        if (node.HasAttribute("WriteTimerInterval"))
                        {
                            WriteInterval = int.Parse(node.GetAttribute("WriteTimerInterval"));
                        }
                        if (node.HasAttribute("WriteCommandDuration"))
                        {
                            WriteCommandDuration = int.Parse(node.GetAttribute("WriteCommandDuration"));
                        }
                    }
                    else if (node.Name == "OutputSettings")
                    {
                        if (node.HasAttribute("OutputDevice"))
                        {
                            var outputDeviceName = node.GetAttribute("OutputDevice");
                            OutputDevice = (OutputDevice)Enum.Parse(typeof(OutputDevice), outputDeviceName);
                        }
                        if (node.HasAttribute("PeakMotionMode"))
                        {
                            PeakMotionMode = node.GetAttribute("PeakMotionMode") == "true";
                        }
                        if (node.HasAttribute("HandyConnectionKey"))
                        {
                            TheHandyConnectionKey = node.GetAttribute("HandyConnectionKey");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("Couldn't load settings: " + ex.Message);
            }
        }
    }

    private void SaveSettings()
    {
        try
        {
            var settingsFilePath = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), settingsFolderName, "settings.xml");
            if (!Directory.Exists(System.IO.Path.GetDirectoryName(settingsFilePath)))
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(settingsFilePath)!);

            var settings = new XmlDocument();
            XmlDeclaration xmlDeclaration = settings.CreateXmlDeclaration("1.0", "UTF-8", null);
            XmlElement root = settings.DocumentElement!;
            settings.InsertBefore(xmlDeclaration, root);

            var rootElement = settings.CreateElement("Teledong");
            settings.AppendChild(rootElement);

            var inputSettingsElement = settings.CreateElement("InputSettings");
            inputSettingsElement.SetAttribute("WriteTimerInterval", WriteInterval.ToString("N0"));
            inputSettingsElement.SetAttribute("WriteCommandDuration", WriteCommandDuration.ToString("N0"));
            rootElement.AppendChild(inputSettingsElement);

            var outputSettingsElement = settings.CreateElement("OutputSettings");
            outputSettingsElement.SetAttribute("OutputDevice", OutputDevice.ToString());
            outputSettingsElement.SetAttribute("PeakMotionMode", PeakMotionMode ? "true" : "false");
            outputSettingsElement.SetAttribute("HandyConnectionKey", TheHandyConnectionKey);
            rootElement.AppendChild(outputSettingsElement);

            settings.Save(settingsFilePath);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Couldn't save settings: " + ex.Message);
        }
    }

    public void UpdatePointerMovement(double pointerYPosition)
    {
        if (InputDeviceIsMouse)
        {
            position = Math.Clamp(position - (pointerYPosition - previousPointerYPosition) / 200.0, 0.0, 1.0);
            previousPointerYPosition = pointerYPosition;
        }
    }
}

public enum OutputDevice : int
{
    None = 0,
    HandyHttpApi = 1,
    ButtplugIo = 2,
    Intiface = 3,
    Funscript = 4
}

public enum InputDevice : int
{
    Teledong = 0,
    Mouse = 1
}

public enum StrokeDirection
{
    None,
    Up,
    Down,
}