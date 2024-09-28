using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;

namespace TeledongCommander.ViewModels;

public partial class FunscriptRecorderViewModel : OutputDeviceViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(FunscriptOutputPathAbbreviated))]
    private string _outputPath;

    public string FunscriptOutputPathAbbreviated => AbbreviatePath(OutputPath);

    [ObservableProperty]
    private string _statusText = "Not recording.";

    FunscriptRecorder funscriptRecorder => (FunscriptRecorder)OutputDevice;
    public override string SettingsId => nameof(FunscriptRecorder);
    DispatcherTimer timer;

    public FunscriptRecorderViewModel(FunscriptRecorder outputDevice) : base(outputDevice)
    {
        Title = "Funscript";
        SecondaryTitle = AbbreviatePath(funscriptRecorder.OutputPath);
        OutputPath = funscriptRecorder.OutputPath;
        
        timer = new DispatcherTimer();
        timer.Interval = TimeSpan.FromSeconds(0.261);
        timer.Tick += Timer_Tick;
        timer.Start();
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        StatusText = funscriptRecorder.StatusText; // todo remove timer and update from event or something
    }

    partial void OnOutputPathChanged(string value)
    {
        funscriptRecorder.OutputPath = value;
        SecondaryTitle = AbbreviatePath(funscriptRecorder.OutputPath);
    }

    protected override void OutputDevice_StatusChanged(object? sender, EventArgs e)
    {
        base.OutputDevice_StatusChanged(sender, e);

        SecondaryTitle = AbbreviatePath(funscriptRecorder.OutputPath);
        StatusText = funscriptRecorder.StatusText;
    }

    protected string AbbreviatePath(string path)
    {
        path = Path.ChangeExtension(path, "");
        return (path.Length > 26 ? "..." : "") + path.Substring(Math.Max(0, path.Length - 26));
    }
}
