using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeledongCommander.ViewModels;

public partial class ButtplugApiViewModel : OutputDeviceViewModel
{
    [ObservableProperty]
    private int _selectedDevice = -1;

    [ObservableProperty]
    private ObservableCollection<string> _devices = new ObservableCollection<string>();

    ButtplugApi buttplugApi => (ButtplugApi)OutputDevice;
    public override string SettingsId => nameof(ButtplugApi);

    public ButtplugApiViewModel(ButtplugApi outputDevice) : base(outputDevice)
    {
        HideFilterStrengthSetting = true;

        Title = "Buttplug/Intiface";
        SecondaryTitle = null;
    }

    partial void OnSelectedDeviceChanged(int value)
    {
        if (SelectedDevice >= 0 && SelectedDevice < Devices.Count)
        {
            SecondaryTitle = Devices[SelectedDevice];
        }
        else
            SecondaryTitle = null;
    }

    protected override void OutputDevice_StatusChanged(object? sender, EventArgs e)
    {
        Devices.Clear();

        if (buttplugApi.IsScanning)
            Devices.Add("[Scanning...]");
        else
        {
            foreach (var deviceName in buttplugApi.DeviceNames)
                Devices.Add(deviceName.Name);
        }

        base.OutputDevice_StatusChanged(sender, e);

        // todo error status etc
    }
}
