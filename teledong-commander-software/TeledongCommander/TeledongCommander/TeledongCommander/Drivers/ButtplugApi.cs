using Buttplug;
using Google.Protobuf.WellKnownTypes;
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
            else if (client.IsScanning)
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
        client = new ButtplugClient("MyClient");
    }

    private async void Processor_Output(object? sender, OutputEventArgs e)
    {
        if (SelectedDeviceIndex >= 0 &&  SelectedDeviceIndex < client.Devices.Count())

        if (client.Devices[SelectedDeviceIndex] is ButtplugClientDevice clientDevice)
            await clientDevice.SendLinearCmd((uint)e.Duration.TotalMilliseconds, e.Position);

        Debug.WriteLine("Sent pos: " + e.Position.ToString("N2") + " , " + e.Duration.TotalMilliseconds);
    }

    public override async Task Start()
    {
        await Stop();

        IsScanning = true;
        TriggerStatusChanged();

        if (!client.Connected)
        {
            var connector = new ButtplugEmbeddedConnectorOptions() { ServerName = "MyServer" };
            await client.ConnectAsync(connector);
        }
        await client.StartScanningAsync();
        await Task.Delay(2000);
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
