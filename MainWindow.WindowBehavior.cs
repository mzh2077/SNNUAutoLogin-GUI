using System;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin;

public sealed partial class MainWindow
{
    private readonly TimedDeviceAction _deviceActions = new();
    private WindowEdgeDock? _edgeDock;
    private int _openDialogs;
    private ContentDialog? _shownDialog;
    private bool _windowActive;

    private void InitializeEdgeDock(IntPtr hwnd)
    {
        var args = Environment.GetCommandLineArgs();
        if (Array.IndexOf(args, "--snapshot") >= 0 || Array.IndexOf(args, "--ui-test") >= 0) return;
        _edgeDock = new WindowEdgeDock(hwnd, DispatcherQueue, () =>
        {
            if (!_uiBuilt || _hiddenToTray || _rt.Exiting || _openDialogs > 0 || _closeHook?.IsInMoveSize == true)
                return true;
            if (Root.XamlRoot is not { } root) return true;
            if (VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Count > 0) return true;
            return _windowActive && FocusManager.GetFocusedElement(root) is TextBox or PasswordBox or NumberBox or ComboBox;
        });
    }

    private async Task<ContentDialogResult> ShowDialogAsync(ContentDialog dialog)
    {
        // WinUI permits one ContentDialog per window. A second async event used to throw
        // from ShowAsync and terminate the process; closing to tray also invalidates XamlRoot.
        if (!_uiBuilt || _rt.Exiting || _shownDialog is not null) return ContentDialogResult.None;
        var root = Root;
        _openDialogs++;
        _shownDialog = dialog;
        _edgeDock?.Reveal();
        try
        {
            dialog.RequestedTheme = Root.ActualTheme;
            // ContentDialog lives in a popup outside Root's resource scope.
            foreach (string key in new[] { "TextControlBorderBrush", "TextControlBorderBrushPointerOver",
                "TextControlBorderBrushFocused", "ComboBoxBorderBrush", "ComboBoxBorderBrushPointerOver" })
                dialog.Resources[key] = Root.Resources[key];
            dialog.Resources["TextControlBorderThemeThickness"] = new Thickness(1);
            dialog.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(1);
            dialog.Resources["ComboBoxBorderThemeThicknessFocused"] = new Thickness(1);
            var result = await dialog.ShowAsync();
            return _uiBuilt && !_rt.Exiting && ReferenceEquals(root, Root)
                ? result : ContentDialogResult.None;
        }
        catch (Exception ex)
        {
            _rt.AddLog("无法显示操作对话框：" + ex.Message);
            return ContentDialogResult.None;
        }
        finally { _shownDialog = null; _openDialogs--; }
    }

    private void ShowDeviceActionResult(DeviceActionOutcome outcome, string success)
    {
        if (outcome == DeviceActionOutcome.Cancelled || _rt.Exiting) return;
        string message = outcome switch
        {
            DeviceActionOutcome.Succeeded => success,
            DeviceActionOutcome.TimedOut => "认证门户未在 8 秒内确认操作结果。请刷新设备列表核对，也可打开设置中的认证门户处理。",
            DeviceActionOutcome.Busy => "上一次设备请求仍在结束，请稍后再试。",
            _ => "操作未完成，请刷新设备列表后重试，也可打开设置中的认证门户处理。"
        };
        _rt.AddLog(message);
        if (!_uiBuilt) return;
        OperationNotice.Severity = outcome == DeviceActionOutcome.Succeeded
            ? InfoBarSeverity.Success : InfoBarSeverity.Warning;
        OperationNotice.Message = message;
        OperationNotice.IsOpen = true;
    }
}
