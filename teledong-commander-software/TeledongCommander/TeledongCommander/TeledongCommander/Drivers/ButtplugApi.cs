using Buttplug;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace TeledongCommander;

public class ButtplugApi
{
    public bool IsConnected => client.Devices.Count() > 0 && client.Connected;

    public string StatusText { 
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
        var connector = new ButtplugEmbeddedConnectorOptions() { ServerName = "MyServer" };
        client = new ButtplugClient("MyClient");
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

    public async Task SendPosition(double position, TimeSpan duration)
    {
        var sendCmdTask = client.Devices.FirstOrDefault()?.SendLinearCmd((uint)duration.TotalMilliseconds, position);
        if (sendCmdTask != null)
            await sendCmdTask;
        Debug.WriteLine("Sent pos: "+position.ToString("N2") + " , " + duration.TotalMilliseconds);
    }

    public void Stop()
    {
        client.StopAllDevicesAsync();
    }

    public void Disconnect()
    {
        try
        {
            Stop();
            client.DisconnectAsync();
        }
        catch
        { }
    }
}
