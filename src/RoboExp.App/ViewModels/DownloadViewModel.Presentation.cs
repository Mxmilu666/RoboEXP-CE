using CommunityToolkit.Mvvm.ComponentModel;
using RoboExp.App.Localization;
using RoboExp.Core.Model;
using Avalonia.Media;
using RoboExp.App.Icons;

namespace RoboExp.App.ViewModels;

public sealed partial class DownloadViewModel
{
    public string FileSizeText { get; }
    public string FileTimeText { get; }
    public string ProgramDirectory => Path.GetDirectoryName(Path.GetFullPath(BinPath))!;
    public string FileName => Path.GetFileName(BinPath);
    public string ControllerName => RobotType.Name;
    public string Step1FullText => Localizer.Instance.T("DownloadX1RCU:label1.Text");
    public string Step2FullText => Localizer.Instance.T("DownloadX1RCU:label2.Text");
    public bool IsStep1Active => ActiveStep == 0;
    public bool IsStep2Active => ActiveStep == 1;
    public bool IsStep3Active => ActiveStep == 2;

    public string Step3Text => RobotType.SoftwareType switch
    {
        SoftwareType.C1RCU => Localizer.Instance.T("Port:Download.WaitPurple"),
        SoftwareType.C6RCU => Localizer.Instance.T("Port:Download.WaitComplete"),
        _ => Localizer.Instance.T("DownloadX1RCU:label3.Text"),
    };

    [ObservableProperty] private bool _isAdvancedOpen;
    [ObservableProperty] private bool _useFixedIp;
    [ObservableProperty] private int _activeStep;
    [ObservableProperty] private string _ipSegment = "192.168.0";
    [ObservableProperty] private string _ipHost = "254";

    public IImage UsbImage => FlatIcons.Command(IsStep1Active ? "DownloadUsb" : "DownloadUsbIdle");
    public IImage PowerImage => FlatIcons.Command(IsStep2Active ? "DownloadPower" : "DownloadPowerIdle");
    public IImage ButtonImage => FlatIcons.Command(IsStep3Active ? "DownloadButton" : "DownloadButtonIdle");

    public string ProgressText => Percent + "%";

    partial void OnActiveStepChanged(int value)
    {
        OnPropertyChanged(nameof(IsStep1Active));
        OnPropertyChanged(nameof(IsStep2Active));
        OnPropertyChanged(nameof(IsStep3Active));
        OnPropertyChanged(nameof(UsbImage));
        OnPropertyChanged(nameof(PowerImage));
        OnPropertyChanged(nameof(ButtonImage));
    }
    partial void OnPercentChanged(int value) => OnPropertyChanged(nameof(ProgressText));
    partial void OnIsDownloadingChanged(bool value)
    {
        OnPropertyChanged(nameof(ProgressText));
    }
    partial void OnIpSegmentChanged(string value) => RefreshIp();
    partial void OnIpHostChanged(string value) => RefreshIp();
    partial void OnUseFixedIpChanged(bool value) => RefreshIp();
    private void RefreshIp()
    {
        FixedIp = UseFixedIp ? IpSegment.Trim() + "." + IpHost.Trim() : "";
        StartCommand.NotifyCanExecuteChanged();
    }
}
