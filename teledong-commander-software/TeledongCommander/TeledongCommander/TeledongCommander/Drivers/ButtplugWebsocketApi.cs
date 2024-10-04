using Buttplug.Client;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;
using Tmds.DBus.Protocol;

namespace TeledongCommander;

public class ButtplugApi : OutputDevice
{
    public int SelectedDeviceIndex { get; set; } = 0;
    public bool IsScanning { get; private set; } = false;
    public IEnumerable<ButtplugClientDevice> DeviceNames => client.Devices;
    public override bool IsStarted => client.Devices.Count() > 0 && client.Connected;

    public override string StatusText { 
        get 
        {
            var deviceCount = client.Devices.Count();

            if (!client.Connected || deviceCount == 0)
                return "No device found";
            else if (IsScanning)
                return "Scanning for devices...";
            else if (deviceCount == 1)
                return "Connected to " + client.Devices.First().Name;
            else
                return deviceCount + " connected devices";
        } 
    }

    ButtplugClient client;

    public ButtplugApi() : base()
    {
        Processor.Output += Processor_Output;
        client = new ButtplugClient("Teledong Commander");
        client.ScanningFinished += Client_ScanningFinished;
        client.ServerDisconnect += Client_ServerDisconnect;
        client.DeviceAdded += Client_DeviceAdded;
        client.DeviceRemoved += Client_DeviceRemoved;
    }

    private void Client_DeviceRemoved(object? sender, DeviceRemovedEventArgs e)
    {
        TriggerStatusChanged();
    }

    private void Client_DeviceAdded(object? sender, DeviceAddedEventArgs e)
    {
        TriggerStatusChanged();
    }

    private async void Client_ServerDisconnect(object? sender, EventArgs e)
    {
        await Stop();
        ErrorMessage = "Server disconnected";
        TriggerStatusChanged();
    }

    private void Client_ScanningFinished(object? sender, EventArgs e)
    {
        IsScanning = false;
        TriggerStatusChanged();
    }

    private async void Processor_Output(object? sender, OutputEventArgs e)
    {
        if (SelectedDeviceIndex < 0 || SelectedDeviceIndex >= client.Devices.Count())
            return;

        try
        {
            if (client.Devices[SelectedDeviceIndex] is ButtplugClientDevice clientDevice)
                await clientDevice.LinearAsync((uint)(e.Duration.TotalMilliseconds * 2.1), e.Position); // Have to multiply duration with 2 or movement is too jagged
        }
        catch (Exception ex)
        {
            var _changed = ErrorMessage == null;
            ErrorMessage = "Failed to send position: " + ex.GetType().ToString();
            if (_changed)
                TriggerStatusChanged();

            return; 
        }

        var changed = ErrorMessage != null;
        ErrorMessage = null;
        if (changed)
            TriggerStatusChanged();

        Debug.WriteLine("Sent pos: " + e.Position.ToString("N2") + " , " + e.Duration.TotalMilliseconds);
    }

    public override async Task Start()
    {
        await Stop();

        ErrorMessage = null;
        IsScanning = true;
        TriggerStatusChanged();

        if (!client.Connected)
        {
            try
            {
                var connector = new ButtplugWebsocketConnector(new Uri("ws://localhost:12345"));
                await client.ConnectAsync(connector);
            }
            catch 
            {
                IsScanning = false;
                ErrorMessage = "Couldn't connect to Websocket";
                TriggerStatusChanged();
                return;
            }
        }
        await client.StartScanningAsync();
        await Task.Delay(10_000);
        await client.StopScanningAsync();

        Debug.WriteLine("Buttplug.io client currently knows about these devices:");
        foreach (var device in client.Devices)
        {
            Debug.WriteLine($"- {device.Name}");
        }
        IsScanning = false;
        TriggerStatusChanged();
    }

    public override async Task Stop()
    {
        try
        {
            if (IsStarted)
            {
                await client.StopAllDevicesAsync();
                await client.DisconnectAsync();
                ErrorMessage = null;
                TriggerStatusChanged();
            }
        }
        catch
        { }
    }
}
