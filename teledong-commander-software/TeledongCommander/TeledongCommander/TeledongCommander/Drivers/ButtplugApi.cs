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

namespace TeledongCommander;

public class ButtplugApi : OutputDevice
{
    public override bool IsConnected => client.Devices.Count() > 0 && client.Connected;

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

    public ButtplugApi()
    {
        Processor = new("buttplugApi");
        Processor.Output += Processor_Output;
        client = new ButtplugClient("MyClient");
    }

    private async void Processor_Output(object? sender, OutputEventArgs e)
    {
        var sendCmdTask = client.Devices.FirstOrDefault()?.SendLinearCmd((uint)e.Duration.TotalMilliseconds, e.Position);
        if (sendCmdTask != null)
            await sendCmdTask;
        Debug.WriteLine("Sent pos: " + e.Position.ToString("N2") + " , " + e.Duration.TotalMilliseconds);
    }

    public override async Task Connect()
    {
        await Disconnect();

        var connector = new ButtplugEmbeddedConnectorOptions() { ServerName = "MyServer" };
        client.ConnectAsync(connector).Wait();
        client.StartScanningAsync().Wait();
        Thread.Sleep(2000);
        client.StopScanningAsync().Wait();

        Debug.WriteLine("Buttplug.io client currently knows about these devices:");
        foreach (var device in client.Devices)
        {
            Debug.WriteLine($"- {device.Name}");
        }
    }

    public async Task Stop()
    {
        await client.StopAllDevicesAsync();
    }

    public override async Task Disconnect()
    {
        try
        {
            await Stop();
            await client.DisconnectAsync();
        }
        catch
        { }
    }
}
