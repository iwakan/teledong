using Avalonia.Controls.Converters;
using Avalonia.Input;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Teledong;
using Avalonia.Media;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;
using Avalonia;
using LiveChartsCore.Defaults;
using Avalonia.Threading;
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
using System.Collections.ObjectModel;

namespace TeledongCommander.ViewModels;

// View model for the main app window
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string inputDeviceStatusText = "";

    [ObservableProperty]
    private bool canClickConnectInputDeviceButton = true;

    [ObservableProperty]
    private bool teledongSunlightMode = false;

    [ObservableProperty]
    private bool teledongKeepPositionOnRelease = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebugMode))]
    private bool infoWindowIsOpen = false;

    public bool DebugMode => InfoWindowIsOpen;

    [ObservableProperty]
    private int teledongFirmwareVersion;

    [ObservableProperty]
    private bool teledongHasBadCalibration = false;

    [ObservableProperty]
    private bool advancedOutputSettingsAreOpen = false;

    [ObservableProperty]
    private bool hasOutputDevices = false;

    [ObservableProperty]
    private List<string> outputDeviceTypes = new()
    {
        "The Handy Key",
        "Funscript Recorder",
        "Local server"
    };

    [ObservableProperty]
    private int selectedOutputDeviceToAdd = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsTeledong))]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsMouse))]
    private InputDevices inputDevice;

    public bool InputDeviceIsTeledong => InputDevice == InputDevices.Teledong;
    public bool InputDeviceIsMouse => InputDevice == InputDevices.Mouse;

    [ObservableProperty]
    private ObservableCollection<UserControl> outputDeviceListItems = new();

    [ObservableProperty]
    private UserControl? outputDeviceSettingsView;

    [ObservableProperty]
    private int selectedOutputDeviceIndex = -1;

    [ObservableProperty]
    private UserControl? advancedOutputSettingsView;

    [ObservableProperty]
    private double readInterval = 100;

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

    const string settingsFolderName = "TeledongCommander";

    Teledong.Teledong teledongApi = new();
    List<OutputDevice> outputDevices = new();

    System.Timers.Timer sensorReadTimer = new();
    DispatcherTimer uiUpdateTimer = new();
    Mutex inputThreadMutex = new();

    DateTime referenceTime = DateTime.Now;
    bool isCalibrating = false;
    DateTime chartStartTime = DateTime.Now - TimeSpan.FromSeconds(10);
    double previousPointerYPosition = 0;
    double position = 0;
    bool skipRead = false;


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

        sensorReadTimer.Interval = 50;
        sensorReadTimer.Elapsed += SensorReadTimer_Tick;

        uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
        uiUpdateTimer.Tick += UiUpdateTimer_Tick;

        LoadSettings();

        sensorReadTimer.Start();
        uiUpdateTimer.Start();

        InputDevice = InputDevices.Teledong;
    }

    private void UiUpdateTimer_Tick(object? sender, EventArgs e)
    {
        // Todo handle this stuff on demand using bindings/events, rather than in this timer. cba right now
        UpdateUiStatus();
    }

    private void UpdateUiStatus()
    {
        if (InputDeviceIsTeledong)
        {
            if (teledongApi.State == TeledongState.NotConnected)
                InputDeviceStatusText = "Not connected";
            else if (teledongApi.State == TeledongState.Calibrating)
                InputDeviceStatusText = "Teledong connected, calibrating...";
            else if (teledongApi.State == TeledongState.Ok)
                InputDeviceStatusText = "Teledong connected, OK";
            else if (teledongApi.State == TeledongState.Error)
                InputDeviceStatusText = "ERROR";

            TeledongHasBadCalibration = teledongApi.BadCalibrationWarning;
        }
        else if (InputDeviceIsMouse)
        {
            InputDeviceStatusText = "Mouse input enabled";
        }
        else
            InputDeviceStatusText = "No input device selected";

        CanClickConnectInputDeviceButton = InputDeviceIsTeledong;
    }

    [RelayCommand]
    private void SetInputDevice(string inputDeviceId)
    {
        InputDevice = (InputDevices)int.Parse(inputDeviceId);
    }

    // Where the magic starts. Sample input and send to output devices.
    private void SensorReadTimer_Tick(object? sender, ElapsedEventArgs e)
    {
        if (isCalibrating || !inputThreadMutex.WaitOne(10))
            return;

        try
        {
            // Get raw position
            if (InputDeviceIsTeledong && teledongApi != null && (teledongApi?.State != TeledongState.NotConnected))
            {
                position = teledongApi?.GetPosition() ?? 1.0;
                SendPointToInputPositionChart(position);

                if (DebugMode)
                {
                    try
                    {
                        string debugString = "";
                        
                        var rawValues = teledongApi!.GetRawSensorValues(false).ToList();
                        var calibratedValues = teledongApi!.GetRawSensorValues(true).ToList();

                        if (rawValues != null && calibratedValues != null)
                        {
                            for (int i = 0; i < rawValues.Count(); i++)
                            {
                                debugString += $"{((int)rawValues[i]).ToString("X2")} ";
                            }
                            debugString += "\n";
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

            // Send raw position to output. The output device processor classes handles filtering/latency etc.
            if (!skipRead)
            {
                foreach (var outputDevice in outputDevices)
                    outputDevice.InputPostion(position);
            }
            skipRead = !skipRead;

        }
        catch (Exception ex)
        {
            Debug.WriteLine("Failed to read sensor: " + ex.Message);
        }
        finally
        {
            inputThreadMutex.ReleaseMutex();
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

    private void SendPointToOutputPositionChart(double newPosition, DateTime time)
    {
        var outputPositionSeriesValues = OutputPositionSeries[0].Values!.Cast<ObservablePoint>().ToList();
        var chartNowTime = (time - chartStartTime);

        outputPositionSeriesValues.Add(new ObservablePoint(chartNowTime.TotalSeconds, newPosition));
        var firstPoint = outputPositionSeriesValues.FirstOrDefault();
        while (firstPoint != null && firstPoint.X < (PositionChartMinX - 5))
        {
            outputPositionSeriesValues.RemoveAt(0);
            firstPoint = outputPositionSeriesValues.FirstOrDefault();
        }

        OutputPositionSeries[0].Values = outputPositionSeriesValues;

        if (chartNowTime.TotalSeconds > PositionChartMaxX)
        {
            PositionChartMinX = (chartNowTime - TimeSpan.FromSeconds(5)).TotalSeconds;
            PositionChartMaxX = chartNowTime.TotalSeconds;
        }
        else
        {
            //Debug.WriteLine("Warning: Chart. " + chartNowTime + "   " + DateTime.Now);
        }
    }

    private void SendPointToOutputPositionChart(double newPosition)
    {
        SendPointToOutputPositionChart(newPosition, DateTime.Now);
    }

    [RelayCommand]
    private void ToggleInfoWindow()
    {
        InfoWindowIsOpen = !InfoWindowIsOpen;
    }

    [RelayCommand]
    private void ToggleAdvancedOutputSettings()
    {
        AdvancedOutputSettingsAreOpen = !AdvancedOutputSettingsAreOpen;
    }

    [RelayCommand]
    private void ConnectInputDevice()
    {
        if (InputDeviceIsTeledong)
        {
            teledongApi ??= new();

            if (teledongApi.Connect())
            {
                try
                {
                    Debug.WriteLine("Found Teledong. Firmware version: " + teledongApi.GetFirmwareVersion());
                }
                catch (Exception ex) { }

                teledongApi.LoadCalibration();

                if (TeledongSunlightMode != teledongApi.IsSunlightMode)
                    TeledongSunlightMode = teledongApi.IsSunlightMode;

                TeledongFirmwareVersion = teledongApi.GetFirmwareVersion();
            }
            else
                Debug.WriteLine("Couldn't connect to Teledong");
        }
    }

    [RelayCommand]
    private void AddOutputDevice()
    {
        OutputDeviceViewModel? dataContext = SelectedOutputDeviceToAdd switch
        {
            0 => new HandyOnlineApiViewModel(new HandyStreamApi()),
            1 => new FunscriptRecorderViewModel(new FunscriptRecorder()),
            2 => new ButtplugApiViewModel(new ButtplugApi()),
            _ => null
        };

        if (dataContext == null)
            return;

        dataContext.Removed += OnOutputDeviceRemoved;

        var outputDevicePreviewView = new OutputDevicePreviewView();
        outputDevicePreviewView.DataContext = dataContext;
        OutputDeviceListItems.Add(outputDevicePreviewView);
        outputDevices.Add(dataContext.OutputDevice);

        HasOutputDevices = OutputDeviceListItems.Count > 0;
    }

    private OutputDeviceViewModel? AddOutputDeviceManually(string typeId)
    {
        OutputDeviceViewModel? dataContext = typeId switch
        {
            nameof(HandyStreamApi) => new HandyOnlineApiViewModel(new HandyStreamApi()),
            nameof(FunscriptRecorder) => new FunscriptRecorderViewModel(new FunscriptRecorder()),
            nameof(ButtplugApi) => new ButtplugApiViewModel(new ButtplugApi()),
            _ => null
        };

        if (dataContext == null)
            return null;

        dataContext.Removed += OnOutputDeviceRemoved;

        var outputDevicePreviewView = new OutputDevicePreviewView();
        outputDevicePreviewView.DataContext = dataContext;
        OutputDeviceListItems.Add(outputDevicePreviewView);
        outputDevices.Add(dataContext.OutputDevice);

        HasOutputDevices = OutputDeviceListItems.Count > 0;

        return dataContext;
    }

    private void OnOutputDeviceRemoved(object? sender, EventArgs e)
    {
        if (sender is not OutputDeviceViewModel outputDeviceViewModel)
            return;

        if (!(OutputDeviceListItems.FirstOrDefault((view) => { return view.DataContext == outputDeviceViewModel; }) is UserControl viewToRemove))
            return;

        OutputDeviceListItems.Remove(viewToRemove);
        outputDevices.Remove(outputDeviceViewModel.OutputDevice);

        HasOutputDevices = OutputDeviceListItems.Count > 0;
    }

    partial void OnSelectedOutputDeviceIndexChanged(int value)
    {
        if (value < 0 || value >= outputDevices.Count)
        {
            OutputDeviceSettingsView = null;
            return;
        }

        var outputDevice = outputDevices[value];

        UserControl settingsView;
        UserControl advancedSettingsView = new AdvancedOutputSettingsView();

        if (outputDevice is HandyStreamApi)
        {
            settingsView = new HandyOnlineApiSettingsView();
        }
        else if (outputDevice is ButtplugApi)
        {
            settingsView = new ButtplugApiSettingsView();
        }
        else if (outputDevice is FunscriptRecorder)
        {
            settingsView = new FunscriptRecorderSettingsView();
        }
        else
            return;

        settingsView.DataContext = advancedSettingsView.DataContext = OutputDeviceListItems[value].DataContext;
        OutputDeviceSettingsView = settingsView;
        AdvancedOutputSettingsView = advancedSettingsView;
    }

    [RelayCommand]
    private void TeledongBootToBootloader()
    {
        if (InputDeviceIsTeledong)
        {
            try
            {
                teledongApi.SendNonQueryCommand(TeledongCommands.EnterBootloader);
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

    partial void OnInputDeviceChanged(InputDevices value)
    {
        DisconnectInputDevice();
        UpdateUiStatus();
    }

    partial void OnReadIntervalChanged(double value)
    {
        sensorReadTimer.Interval = value;
    }

    partial void OnTeledongSunlightModeChanged(bool value)
    {
        teledongApi.SetSunlightMode(value);
    }

    partial void OnTeledongKeepPositionOnReleaseChanged(bool value)
    {
        teledongApi.KeepPositionAtRelease = value;
    }

    private void DisconnectInputDevice()
    {
        teledongApi.Disconnect();
    }

    [RelayCommand]
    private async Task CalibrateTeledong()
    {
        if (teledongApi == null || teledongApi.State == TeledongState.NotConnected || isCalibrating)
            return;

        isCalibrating = true;
        try
        {
            await (teledongApi.CalibrateAsync() ?? Task.CompletedTask);

            if (TeledongSunlightMode != teledongApi.IsSunlightMode)
                TeledongSunlightMode = teledongApi.IsSunlightMode;
        }
        catch (Exception ex)
        {
            Debug.Write("Failed to calibrate teledong: " + ex.Message);
        }
        isCalibrating = false;
    }

    private void DisconnectOutputDevice()
    {
        foreach (var device in outputDevices)
            device.Stop();
    }

    public void SaveAndFree()
    {
        sensorReadTimer.Stop();
        Thread.Sleep(200);

        SaveSettings();
        DisconnectInputDevice();
        DisconnectOutputDevice();
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
                        if (node.HasAttribute("SampleRate"))
                        {
                            sensorReadTimer.Interval = int.Max(int.Parse(node.GetAttribute("SampleRate")) / 2, 10);
                        }
                    }
                    else if (node.Name == "OutputDevices")
                    {
                        foreach (XmlElement outputDeviceNode in node.ChildNodes)
                        {
                            var outputDeviceViewModel = AddOutputDeviceManually(outputDeviceNode.Name);
                            if (outputDeviceViewModel == null)
                                continue;

                            if (outputDeviceNode.HasAttribute("FilterStrength"))
                            {
                                outputDeviceViewModel.FilterStrength = float.Parse(outputDeviceNode.GetAttribute("FilterStrength"));
                            }
                            if (outputDeviceNode.HasAttribute("FilterTime"))
                            {
                                outputDeviceViewModel.FilterTimeMilliseconds = float.Parse(outputDeviceNode.GetAttribute("FilterTime"));
                            }
                            if (outputDeviceNode.HasAttribute("PeakMotionMode"))
                            {
                                outputDeviceViewModel.PeakMotionMode = outputDeviceNode.GetAttribute("PeakMotionMode") == "true";
                            }

                            if (outputDeviceViewModel is FunscriptRecorderViewModel funscriptRecorderViewModel)
                            {
                                if (outputDeviceNode.HasAttribute("OutputPath"))
                                {
                                    funscriptRecorderViewModel.OutputPath = outputDeviceNode.GetAttribute("OutputPath");
                                }
                            }
                            if (outputDeviceViewModel is HandyOnlineApiViewModel handyOnlineApiViewModel)
                            {
                                if (outputDeviceNode.HasAttribute("ConnectionKey"))
                                {
                                    handyOnlineApiViewModel.TheHandyConnectionKey = outputDeviceNode.GetAttribute("ConnectionKey");
                                }
                            }
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
            var settingsFilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), settingsFolderName, "settings.xml");
            if (!Directory.Exists(Path.GetDirectoryName(settingsFilePath)))
                Directory.CreateDirectory(Path.GetDirectoryName(settingsFilePath)!);

            var settings = new XmlDocument();
            XmlDeclaration xmlDeclaration = settings.CreateXmlDeclaration("1.0", "UTF-8", null);
            XmlElement root = settings.DocumentElement!;
            settings.InsertBefore(xmlDeclaration, root);

            var rootElement = settings.CreateElement("Teledong");
            settings.AppendChild(rootElement);

            var inputSettingsElement = settings.CreateElement("InputSettings");
            inputSettingsElement.SetAttribute("SampleRate", (sensorReadTimer.Interval * 2).ToString("N0"));
            rootElement.AppendChild(inputSettingsElement);

            var outputDevicesElement = settings.CreateElement("OutputDevices");

            foreach (var outputDeviceView in OutputDeviceListItems)
            {
                var outputDeviceViewModel = (OutputDeviceViewModel)outputDeviceView.DataContext!;

                var outputDeviceElement = settings.CreateElement(outputDeviceViewModel.SettingsId); 
                outputDeviceElement.SetAttribute("PeakMotionMode", outputDeviceViewModel.PeakMotionMode ? "true" : "false");
                outputDeviceElement.SetAttribute("FilterTime", outputDeviceViewModel.FilterTimeMilliseconds.ToString("N0"));
                outputDeviceElement.SetAttribute("FilterStrength", outputDeviceViewModel.FilterStrength.ToString("N3"));

                if (outputDeviceViewModel is HandyOnlineApiViewModel handyApi)
                    outputDeviceElement.SetAttribute("ConnectionKey", handyApi.TheHandyConnectionKey);
                else if (outputDeviceViewModel is FunscriptRecorderViewModel funscriptRecorder)
                    outputDeviceElement.SetAttribute("OutputPath", funscriptRecorder.OutputPath);

                outputDevicesElement.AppendChild(outputDeviceElement);
            }
            rootElement.AppendChild(outputDevicesElement);

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

public struct StrokerPoint
{
    public StrokerPoint(double position, TimeSpan time)
    {
        Position = position;
        Time = time;
    }

    public double Position; // From 0 to 1
    public TimeSpan Time;

    public double X => Position;
    public double Y => Time.TotalSeconds;

    public StrokerPoint AddLatency(double latency)
    {
        return new StrokerPoint(Position, Time.Add(TimeSpan.FromMilliseconds(latency)));
    }

    public override bool Equals(object obj)
    {
        if (obj is StrokerPoint point)
        {
            if (this.Position != point.Position || this.Time != point.Time) 
                return false;
            return true;
        }
        return false;
    }

    public override int GetHashCode()
    {
        return base.GetHashCode();
    }
}

public enum InputDevices : int
{
    Teledong = 0,
    Mouse = 1
}