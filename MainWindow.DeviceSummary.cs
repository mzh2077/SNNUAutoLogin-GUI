using System;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin;

public sealed partial class MainWindow
{
    private int ReadGuardThreshold()
    {
        if (int.TryParse(DeviceGuardThresholdBox.Text, out int typed))
            return Math.Clamp(typed, 1, 50);
        return double.IsFinite(DeviceGuardThresholdBox.Value)
            ? (int)Math.Clamp(Math.Round(DeviceGuardThresholdBox.Value), 1, 50)
            : _rt.Store.Settings.DeviceGuardThreshold;
    }

    private void UpdateGuardSummarySettings()
    {
        if (!_uiReady || !_uiBuilt) return;
        _rt.Store.Settings.DeviceGuardEnabled = DeviceGuardSwitch.IsOn;
        _rt.Store.Settings.DeviceGuardThreshold = ReadGuardThreshold();
        RefreshDeviceSummary();
        ScheduleSave();
    }

    private void RefreshDeviceSummary()
    {
        if (!_uiBuilt) return;
        var settings = _rt.Store.Settings;
        DeviceSummary.Text = DescribeDevices(_rt.Devices.State, _rt.Devices.Devices.Count,
            settings.DeviceGuardEnabled, settings.DeviceGuardThreshold);
    }

    private static string DescribeDevices(DeviceListState state, int count, bool enabled, int threshold)
    {
        if (state == DeviceListState.Idle) return "尚未读取在线设备";
        if (count == 0) return state switch
        {
            DeviceListState.Loading => "正在读取在线设备…",
            DeviceListState.Failed => "在线设备暂时读不到",
            _ => "当前没有在线设备"
        };
        return enabled ? $"当前 {count} 台设备在线 · 守卫阈值 {threshold} 台" : $"当前 {count} 台设备在线";
    }
}
