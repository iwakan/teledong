using LiveChartsCore.SkiaSharpView;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView.Painting;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Teledong;
using SkiaSharp;
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
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Reflection;

namespace TeledongCommander.ViewModels;

// View model for the main app window
public partial class MainViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _inputDeviceStatusText = "Not connected";

    [ObservableProperty]
    private bool _canClickConnectInputDeviceButton = true;

    [ObservableProperty]
    private bool _teledongSunlightMode = false;

    [ObservableProperty]
    private bool _teledongKeepPositionOnRelease = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DebugMode))]
    private bool _infoWindowIsOpen = false;

    public bool DebugMode => InfoWindowIsOpen;

    [ObservableProperty]
    private string? _teledongFirmwareVersion = null;

    [ObservableProperty]
    private bool _teledongIsCalibrating = false;

    [ObservableProperty]
    private bool _teledongHasBadCalibration = false;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdvancedOutputSettingsIsVisible))]
    private bool _advancedOutputSettingsAreOpen = false;

    public bool AdvancedOutputSettingsIsVisible => AdvancedOutputSettingsAreOpen && SelectedOutputDevice != null;

    [ObservableProperty]
    private string _handyManualAuthKey = "";

    public bool HasOutputDevices => OutputDevices.Any();

    [ObservableProperty]
    private List<string> _outputDeviceTypes = new()
    {
        "The Handy Key",
        "Funscript Recorder",
        "Intiface/Buttplug.io"
    };

    [ObservableProperty]
    private int _selectedOutputDeviceToAdd = 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsTeledong))]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsMouse))]
    private InputDevices _inputDevice;

    public bool InputDeviceIsTeledong => InputDevice == InputDevices.Teledong;
    public bool InputDeviceIsMouse => InputDevice == InputDevices.Mouse;

    public bool TeledongIsConnected => InputDeviceIsTeledong && teledongApi.State != TeledongState.NotConnected;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasOutputDevices))]
    private ObservableCollection<OutputDeviceViewModel> _outputDevices = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AdvancedOutputSettingsIsVisible))]
    private OutputDeviceViewModel? _selectedOutputDevice;

    [ObservableProperty]
    private double _readInterval = 100;

    [ObservableProperty]
    private string _sensorValuesDebugString = "";

    [ObservableProperty]
    private ISeries[] _inputPositionSeries = new ISeries[]
    {
        // Todo move this to view, viewmodel should not decide appearance
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
    private ISeries[] _outputPositionSeries = new ISeries[]
    {
        // Todo move this to view, viewmodel should not decide appearance
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
    private Axis[] _positionChartXAxes;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionChartXAxes))]
    private double? _positionChartMinX;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PositionChartXAxes))]
    private double? _positionChartMaxX;

    [ObservableProperty]
    private string? _inputDeviceError;

    [ObservableProperty]
    private string? _newVersion;

    [ObservableProperty]
    private string? _newFirmwareVersion;

    [ObservableProperty]
    private string? _currentVersion;

    [ObservableProperty]
    private bool _hasNewVersion = false;


    const string settingsFolderName = "TeledongCommander";

    Teledong.Teledong teledongApi = new();

    System.Timers.Timer sensorReadTimer = new();
    Mutex inputThreadMutex = new();

    DateTime referenceTime = DateTime.Now;
    bool isCalibrating = false;
    DateTime chartStartTime = DateTime.Now - TimeSpan.FromSeconds(10);
    double previousPointerYPosition = 0;
    double position = 0;
    bool skipRead = false;


    public MainViewModel()
    {
        CurrentVersion = Assembly.GetExecutingAssembly().GetName().Version!.ToString().Split('-')[0][0..^2];
        CheckForUpdates();

        PositionChartXAxes = new Axis[]
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

        OutputDevices.CollectionChanged += (sender, e) => { OnPropertyChanged(nameof(HasOutputDevices)); };

        sensorReadTimer.Interval = 50;
        sensorReadTimer.Elapsed += SensorReadTimer_Tick;

        LoadSettings();

        sensorReadTimer.Start();

        InputDevice = InputDevices.Teledong;
    }


    private void UpdateUiStatus()
    {
        TeledongIsCalibrating = false;
        if (InputDeviceIsTeledong)
        {
            if (teledongApi.State == TeledongState.NotConnected)
                InputDeviceStatusText = "Not connected";
            else if (teledongApi.State == TeledongState.Calibrating)
            {
                InputDeviceStatusText = "Teledong connected, calibrating...";
                TeledongIsCalibrating = true;
            }
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
        UpdateUiStatus();

        if (isCalibrating || !inputThreadMutex.WaitOne(10))
            return;

        try
        {
            // Get raw position
            if (InputDeviceIsTeledong && teledongApi != null && (teledongApi?.State != TeledongState.NotConnected))
            {
                position = teledongApi?.GetPosition() ?? 1.0;
                if (!skipRead)
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
                if (!skipRead)
                    SendPointToInputPositionChart(position);
            }

            // Send raw position to output. The output device processor classes handles filtering/latency etc.
            // We skip doing this every other cycle, because the Teledong timer is designed to work at 50ms intervals, but we only need to output every 100ms or so.
            if (!skipRead)
            {
                foreach (var outputDevice in OutputDevices)
                    outputDevice.OutputDevice.InputPostion(position);
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
    private void ShowHelp()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://teledong.com/teledong-commander#tutorial",
            UseShellExecute = true
        });
    }

    [RelayCommand]
    private void ToggleAdvancedOutputSettings()
    {
        AdvancedOutputSettingsAreOpen = !AdvancedOutputSettingsAreOpen;
    }

    [RelayCommand]
    private async Task ConnectInputDevice()
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

                TeledongFirmwareVersion = teledongApi.GetFirmwareVersion().ToString();
                InputDeviceError = null;
                await CheckForFirmwareUpdates();
            }
            else
            { 
                Debug.WriteLine("Couldn't connect to Teledong");
                InputDeviceError = "Couldn't connect to Teledong.";
            }
        }
        else
            InputDeviceError = null;
    }

    [RelayCommand]
    private void AddOutputDevice()
    {
        OutputDeviceViewModel? outputDeviceViewModel = SelectedOutputDeviceToAdd switch
        {
            0 => new HandyOnlineApiViewModel(new HandyStreamApi() { ApiKey = (HandyManualAuthKey.Length > 10 ? HandyManualAuthKey : "")}),
            1 => new FunscriptRecorderViewModel(new FunscriptRecorder()),
            2 => new ButtplugApiViewModel(new ButtplugApi()),
            _ => null
        };

        if (outputDeviceViewModel == null)
            return;

        outputDeviceViewModel.Removed += OnOutputDeviceRemoved;

        var outputDevicePreviewView = new OutputDevicePreviewView();
        outputDevicePreviewView.DataContext = outputDeviceViewModel;
        OutputDevices.Add(outputDeviceViewModel);

        SelectedOutputDevice = outputDeviceViewModel;
    }

    private OutputDeviceViewModel? AddOutputDeviceManually(string typeId)
    {
        OutputDeviceViewModel? outputDeviceViewModel = typeId switch
        {
            nameof(HandyStreamApi) => new HandyOnlineApiViewModel(new HandyStreamApi() { ApiKey = (HandyManualAuthKey.Length > 10 ? HandyManualAuthKey : "") }),
            nameof(FunscriptRecorder) => new FunscriptRecorderViewModel(new FunscriptRecorder()),
            nameof(ButtplugApi) => new ButtplugApiViewModel(new ButtplugApi()),
            _ => null
        };

        if (outputDeviceViewModel == null)
            return null;

        outputDeviceViewModel.Removed += OnOutputDeviceRemoved;

        OutputDevices.Add(outputDeviceViewModel);

        if (OutputDevices.Count == 1)
            SelectedOutputDevice = outputDeviceViewModel;

        return outputDeviceViewModel;
    }

    private void OnOutputDeviceRemoved(object? sender, EventArgs e)
    {
        if (sender is not OutputDeviceViewModel outputDeviceViewModel)
            return;

        if (!(OutputDevices.FirstOrDefault((view) => { return view == outputDeviceViewModel; }) is OutputDeviceViewModel outputDeviceToRemove))
            return;

        OutputDevices.Remove(outputDeviceToRemove);
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
        InputDeviceError = null;
        DisconnectInputDevice();
        UpdateUiStatus();
    }

    partial void OnReadIntervalChanged(double value)
    {
        sensorReadTimer.Interval = value / 2.0;
    }

    partial void OnTeledongSunlightModeChanged(bool value)
    {
        teledongApi.SetSunlightMode(value);
    }

    partial void OnTeledongKeepPositionOnReleaseChanged(bool value)
    {
        teledongApi.KeepPositionAtRelease = value;
    }

    partial void OnHandyManualAuthKeyChanged(string value)
    {
        foreach (var device in OutputDevices)
        {
            if (device is HandyOnlineApiViewModel handyDevice)
            {
                var internalDevice = handyDevice.OutputDevice as HandyStreamApi;
                if (internalDevice == null)
                    continue;

                internalDevice.Stop();
                internalDevice.ApiKey = HandyManualAuthKey;
            }
        }
    }

    [RelayCommand]
    protected void IncreaseReadInterval()
    {
        ReadInterval += 10;
    }

    [RelayCommand]
    protected void DecreaseReadInterval()
    {
        if (ReadInterval > 30)
            ReadInterval -= 10;
        else
            ReadInterval = 0;
    }

    private void DisconnectInputDevice()
    {
        teledongApi.Disconnect();
    }

    [RelayCommand]
    private async Task CalibrateTeledong()
    {
        if (teledongApi == null || teledongApi.State == TeledongState.NotConnected || isCalibrating)
        {
            InputDeviceError = "Teledong must be connected before calibrating.";
            return;
        }

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
            InputDeviceError = "Failure during Teledong calibration.";
        }
        isCalibrating = false;
        InputDeviceError = null;
    }

    [RelayCommand]
    public async Task CheckForUpdates()
    {
        try
        {
            using var client = new HttpClient();
            var newVersion = await client.GetStringAsync("https://raw.githubusercontent.com/iwakan/teledong/main/commander-version");
            var currentVersion = CurrentVersion;
            if (newVersion != null && newVersion.Length < 20)
                NewVersion = newVersion;
            else
            {
                NewVersion = null;
                throw new Exception("Unexpected version value");
            }

            if (NewVersion != null && CurrentVersion != NewVersion)
            {
                HasNewVersion = true;
            }
        }
        catch (Exception ex)
        {
            NewVersion = null;
        }
    }

    [RelayCommand]
    public async Task CheckForFirmwareUpdates()
    {
        try
        {
            using var client = new HttpClient();
            var newFirmwareVersion = await client.GetStringAsync("https://raw.githubusercontent.com/iwakan/teledong/main/firmware-version");
            var currentVersion = TeledongFirmwareVersion;
            if (newFirmwareVersion != null && newFirmwareVersion.Length < 20)
                NewFirmwareVersion = newFirmwareVersion;
            else
            {
                NewFirmwareVersion = null;
                throw new Exception("Unexpected version value");
            }
        }
        catch (Exception ex)
        {
            NewFirmwareVersion = null;
        }
    }

    [RelayCommand]
    public void OpenWebsite()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://teledong.com/teledong-commander#download",
            UseShellExecute = true
        });
    }

    private void DisconnectOutputDevice()
    {
        foreach (var deviceModel in OutputDevices)
            deviceModel.OutputDevice.Stop();
    }

    public void SaveAndFree()
    {
        sensorReadTimer.Stop();
        Thread.Sleep(200);

        SaveSettings();
        DisconnectInputDevice();
        DisconnectOutputDevice();
    }

    partial void OnInfoWindowIsOpenChanged(bool value)
    {
        if (value == false)
            SaveSettings();
    }

    private void LoadSettings()
    {
        // Todo refactor this, use JsonSerializer
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
                            ReadInterval = int.Max(int.Parse(node.GetAttribute("SampleRate")), 20);
                        }
                    }
                    else if (node.Name == "CommonOutputSettings")
                    {
                        if (node.HasAttribute("HandyManualAuthKey"))
                        {
                            HandyManualAuthKey = node.GetAttribute("HandyManualAuthKey");
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
                            else
                                outputDeviceViewModel.FilterTimeMilliseconds = 400;

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
            // Todo refactor this, use JsonSerializer
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
            inputSettingsElement.SetAttribute("SampleRate", ReadInterval.ToString("N0"));
            rootElement.AppendChild(inputSettingsElement);

            var outputSettingsElement = settings.CreateElement("CommonOutputSettings");
            outputSettingsElement.SetAttribute("HandyManualAuthKey", HandyManualAuthKey);
            rootElement.AppendChild(outputSettingsElement);

            var outputDevicesElement = settings.CreateElement("OutputDevices");

            foreach (var outputDeviceViewModel in OutputDevices)
            {
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
    /// <summary>
    /// From 0 to 1
    /// </summary>
    public double Position { get; }
    /// <summary>
    /// Time it should take to move to this position. Optional.
    /// </summary>
    public TimeSpan Time { get; }

    public double X => Position;
    public double Y => Time.TotalSeconds;

    public StrokerPoint(double position, TimeSpan time)
    {
        Position = position;
        Time = time;
    }

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