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
using Avalonia.Controls.Templates;
using Avalonia.Platform.Storage;
using Splat;
using System.Collections.ObjectModel;

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
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsHandyKey))]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsButtplugIo))]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsIntiface))]
    [NotifyPropertyChangedFor(nameof(OutputDeviceIsFunscript))]
    private OutputDevice outputDevice;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsTeledong))]
    [NotifyPropertyChangedFor(nameof(InputDeviceIsMouse))]
    private InputDevices inputDevice;

    public bool OutputDeviceIsHandyKey => OutputDevice is HandyOnlineApi;
    public bool OutputDeviceIsButtplugIo => OutputDevice is ButtplugApi;
    public bool OutputDeviceIsIntiface => false;
    public bool OutputDeviceIsFunscript => OutputDevice is FunscriptRecorder;

    public bool InputDeviceIsTeledong => InputDevice == InputDevices.Teledong;
    public bool InputDeviceIsMouse => InputDevice == InputDevices.Mouse;

    [ObservableProperty]
    private ObservableCollection<UserControl> outputDeviceListItems = new();

    [ObservableProperty]
    private double readInterval = 50;

    [ObservableProperty]
    private double writeInterval = 50;

    [ObservableProperty]
    private double writeCommandDuration = 250;

    [ObservableProperty]
    private double filterEpsilon = 0.05;

    [ObservableProperty]
    private double filterTimeMilliseconds = 300;

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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunscriptOutputPathAbbreviated))]
    private string funscriptOutputPath;

    public string FunscriptOutputPathAbbreviated => (FunscriptOutputPath.Length > 22 ? "..." : "") + FunscriptOutputPath.Substring(Math.Max(0, FunscriptOutputPath.Length - 22));

    TeledongManager? teledongApi;
    ButtplugApi buttplugIoApi = new();
    HandyOnlineApi handyOnlineApi = new();
    FunscriptRecorder funscriptRecorder = new();

    System.Timers.Timer sensorReadTimer = new();
    System.Timers.Timer writeTimer = new();
    DispatcherTimer uiUpdateTimer = new();
    Mutex outputThreadMutex = new();
    Mutex inputThreadMutex = new();

    List<StrokerPoint> inputPointBuffer = new List<StrokerPoint>();
    Queue<StrokerPoint> outputPointBuffer = new Queue<StrokerPoint>();
    DateTime referenceTime = DateTime.Now;

    const string settingsFolderName = "TeledongCommander";

    double position = 0;
    double previousPosition = 0.9;
    double previousPointerYPosition = 0;

    StrokeDirection currentDirection = StrokeDirection.None;
    DateTime lastWriteTime = DateTime.Now;

    int writeCommandsOngoing = 0;
    bool isCalibrating = false;
    DateTime chartStartTime = DateTime.Now - TimeSpan.FromSeconds(10);

    Dictionary<OutputDevices, OutputDevice> outputDeviceModels;

    public MainViewModel()
    {
        outputDeviceModels = new()
        {
            [OutputDevices.ButtplugIo] = buttplugIoApi,
            [OutputDevices.HandyHttpApi] = handyOnlineApi,
            [OutputDevices.Funscript] = funscriptRecorder
        };

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

        FunscriptOutputPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), $"teledong_{DateTime.Now.ToShortDateString()}.funscript");

        sensorReadTimer.Interval = 50;
        sensorReadTimer.Elapsed += SensorReadTimer_Tick;
        sensorReadTimer.Elapsed += WriteTimer_Tick;

        writeTimer.Interval = 50;
        writeTimer.Elapsed += WriteTimer_Tick;

        uiUpdateTimer.Interval = TimeSpan.FromMilliseconds(500);
        uiUpdateTimer.Tick += UiUpdateTimer_Tick;

        LoadSettings();

        //writeTimer.Start();
        sensorReadTimer.Start();
        uiUpdateTimer.Start();

        OutputDevice = outputDeviceModels[OutputDevices.HandyHttpApi];
        InputDevice = InputDevices.Teledong;
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
            if (teledongApi == null || teledongApi.State == TeledongState.NotConnected)
                InputDeviceStatusText = "Not connected";
            else if (teledongApi.State == TeledongState.Calibrating)
                InputDeviceStatusText = "Teledong connected, calibrating...";
            else if (teledongApi.State == TeledongState.Ok)
                InputDeviceStatusText = "Teledong connected, OK";
            else if (teledongApi.State == TeledongState.Error)
                InputDeviceStatusText = "ERROR";
        }
        else if (InputDeviceIsMouse)
        {
            InputDeviceStatusText = "Mouse input enabled";
        }
        else
            InputDeviceStatusText = "No input device selected";

        OutputDeviceStatusText = OutputDevice.StatusText ?? "Not connected";

        CanClickConnectInputDeviceButton = InputDeviceIsTeledong;
        CanClickConnectOutputDeviceButton = true;
    }


    [RelayCommand]
    private void SetOutputDevice(string outputDeviceId)
    {
        OutputDevice = outputDeviceModels[(OutputDevices)int.Parse(outputDeviceId)];

        if (OutputDeviceIsHandyKey || OutputDeviceIsFunscript)
            PeakMotionMode = true;
        else
            PeakMotionMode = false;
    }

    [RelayCommand]
    private void SetInputDevice(string inputDeviceId)
    {
        InputDevice = (InputDevices)int.Parse(inputDeviceId);
    }

    private void WriteTimer_Tick(object? sender, ElapsedEventArgs e)
    {
        if (!outputThreadMutex.WaitOne(10))
            return;
        
        try
        {
            TimeSpan outputTimeThreshold = DateTime.Now - referenceTime - TimeSpan.FromMilliseconds(FilterTimeMilliseconds);

            StrokerPoint nextPoint = default;
            bool shouldOutput = false;

            while (outputPointBuffer.TryPeek(out StrokerPoint potentialNextPoint))
            {
                if (potentialNextPoint.Time <= outputTimeThreshold)
                {
                    nextPoint = outputPointBuffer.Dequeue();
                    shouldOutput = true;
                    //Debug.WriteLine($"POINT TIME 1: {nextPoint.Time} - Thread: {Thread.CurrentThread.ManagedThreadId}");
                }
                else
                    break;
            }

            if (shouldOutput)
            {
                TimeSpan writeDuration = DateTime.Now - lastWriteTime - TimeSpan.FromMilliseconds(1);
                lastWriteTime = DateTime.Now;

                bool didOutput = true;

                if (writeDuration.TotalSeconds < 2)
                {
                    OutputDevice?.SendPosition(Math.Clamp(nextPoint.Position, 0, 1), writeDuration);

                    if (OutputDeviceIsFunscript)
                    {
                        if (!funscriptRecorder.IsRecording)
                            didOutput = false;
                    }

                    /*if (OutputDeviceIsButtplugIo && (buttplugIoApi?.IsConnected ?? false))
                    {
                        buttplugIoApi?.SendPosition(Math.Clamp(nextPoint.Position, 0, 1), writeDuration * 2.0);//TimeSpan.FromMilliseconds(WriteCommandDuration));
                    }
                    else if (OutputDeviceIsHandyKey && (handyOnlineApi?.IsConnected ?? false))
                    {
                        handyOnlineApi?.SendPosition(Math.Clamp(nextPoint.Position, 0, 1), writeDuration);//TimeSpan.FromMilliseconds(WriteCommandDuration));
                    }
                    else if (OutputDeviceIsFunscript)
                    {
                        if (funscriptRecorder?.IsRecording ?? false)
                        {
                            funscriptRecorder?.PutPosition(nextPoint.Position);
                            //Debug.WriteLine($"POINT TIME 2: {nextPoint.Time} - Thread: {Thread.CurrentThread.ManagedThreadId}");
                        }
                        else
                            didOutput = false;
                    }*/
                }
                else
                    didOutput = false;

                if (didOutput)
                    SendPointToOutputPositionChart(nextPoint.Position, referenceTime + nextPoint.Time);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Failed to output value: " + ex.Message);
        }
        finally
        {
            outputThreadMutex.ReleaseMutex();
        }

        return;


        bool shouldSendPosition = false;
        double positionToSend = 1.0;

        if (!PeakMotionMode)
        {
            // Direct forwarding, write a new position to the output device if the input position has changed
            var difference = Math.Abs(position - previousPosition);
            if (difference > 0.02 || DateTime.Now - lastWriteTime > TimeSpan.FromSeconds(1))
            {
                shouldSendPosition = true;
                lastWriteTime = DateTime.Now;
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

            OutputDevice.SendPosition(Math.Clamp(positionToSend, 0, 1), TimeSpan.FromMilliseconds(WriteCommandDuration));

            /*if (OutputDeviceIsButtplugIo && (buttplugIoApi?.IsConnected ?? false))
            {
                buttplugIoApi?.SendPosition(Math.Clamp(positionToSend, 0, 1), TimeSpan.FromMilliseconds(WriteCommandDuration));
                SendPointToOutputPositionChart(positionToSend);
            }
            else if (OutputDeviceIsHandyKey && (handyOnlineApi?.IsConnected ?? false))
            {
                handyOnlineApi?.SendPosition(Math.Clamp(positionToSend, 0, 1), TimeSpan.FromMilliseconds(WriteCommandDuration));
                SendPointToOutputPositionChart(positionToSend);
            }
            else if (OutputDeviceIsFunscript)
            {
                if (funscriptRecorder?.IsRecording ?? false)
                {
                    funscriptRecorder?.PutPosition(positionToSend);
                    SendPointToOutputPositionChart(positionToSend);
                }
            }*/

            writeCommandsOngoing--;
        }
    }

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

            // Filtering before queueing output
            var now = DateTime.Now - referenceTime;

            inputPointBuffer.Add(new StrokerPoint(position, now));

            if ((now - inputPointBuffer.First().Time) > TimeSpan.FromMilliseconds(FilterTimeMilliseconds))
            {
                List<StrokerPoint> processedPointBuffer = RamerDouglasPeuckerNetV2.RamerDouglasPeucker.Reduce(inputPointBuffer.ToList(), FilterEpsilon);
                Debug.WriteLine($"Reduced from {inputPointBuffer.Count} points to {processedPointBuffer.Count} points.");

                foreach (var point in processedPointBuffer.Skip(1))
                {
                    outputPointBuffer.Enqueue(point);
                }

                inputPointBuffer.Clear();
            }

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
    private void ToggleAdvancedSettings()
    {
        AdvancedSettingsAreOpen = !AdvancedSettingsAreOpen;
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

                if (TeledongSunlightMode != teledongApi.SunlightMode)
                    TeledongSunlightMode = teledongApi.SunlightMode;

                TeledongFirmwareVersion = teledongApi.GetFirmwareVersion();
            }
            else
                Debug.WriteLine("Couldn't connect to Teledong");
        }
    }

    partial void OnTheHandyConnectionKeyChanged(string value)
    {
        handyOnlineApi.ConnectionKey = value;
    }

    [RelayCommand]
    private async Task ConnectOutputDevice()
    {
        DisconnectOutputDevice();

        await OutputDevice.Connect();
    }

    [RelayCommand]
    private void StopAndSaveFunscriptRecording()
    {
        funscriptRecorder?.StopRecording();
    }

    [RelayCommand]
    private void TeledongBootToBootloader()
    {
        if (InputDeviceIsTeledong)
        {
            try
            {
                teledongApi?.SendNonQueryCommand(TeledongCommands.EnterBootloader);
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

    partial void OnOutputDeviceChanged(OutputDevice value)
    {
        DisconnectOutputDevice();
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
        teledongApi?.SetSunlightMode(value);
    }

    partial void OnFunscriptOutputPathChanged(string value)
    {
        if (funscriptRecorder != null)
            funscriptRecorder.OutputPath = value;
    }

    private void DisconnectInputDevice()
    {
        teledongApi?.Disconnect();
    }

    [RelayCommand]
    private async Task CalibrateTeledong()
    {
        if (teledongApi == null || teledongApi.State == TeledongState.NotConnected || isCalibrating)
            return;

        isCalibrating = true;
        try
        {
            await (teledongApi?.CalibrateAsync() ?? Task.CompletedTask);

            if (TeledongSunlightMode != teledongApi.SunlightMode)
                TeledongSunlightMode = teledongApi.SunlightMode;
        }
        catch (Exception ex)
        {
            Debug.Write("Failed to calibrate teledong: " + ex.Message);
        }
        isCalibrating = false;
    }

    private void DisconnectOutputDevice()
    {
        foreach (var device in outputDeviceModels)
            device.Value.Disconnect();
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
                            OutputDevice = outputDeviceModels[(OutputDevices)Enum.Parse(typeof(OutputDevices), outputDeviceName)];
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

public enum OutputDevices : int
{
    None = 0,
    HandyHttpApi = 1,
    ButtplugIo = 2,
    Intiface = 3,
    Funscript = 4
}

public enum InputDevices : int
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