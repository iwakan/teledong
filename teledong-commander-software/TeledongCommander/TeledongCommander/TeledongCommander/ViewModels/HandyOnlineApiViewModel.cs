using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace TeledongCommander.ViewModels;

public partial class HandyOnlineApiViewModel : OutputDeviceViewModel
{
    [ObservableProperty]
    private string theHandyConnectionKey = "";

    HandyOnlineApi handyApi => (HandyOnlineApi)OutputDevice;
    public override string SettingsId => nameof(HandyOnlineApi);

    public HandyOnlineApiViewModel(HandyOnlineApi outputDevice) : base(outputDevice)
    {
        Title = "The Handy Connection Key";
        SecondaryTitle = outputDevice.ConnectionKey;
    }

    protected override void OutputDevice_StatusChanged(object? sender, EventArgs e)
    {
        base.OutputDevice_StatusChanged(sender, e);

        SecondaryTitle = handyApi.ConnectionKey;
    }

    partial void OnTheHandyConnectionKeyChanged(string value)
    {
        handyApi.ConnectionKey = value;
    }
}
