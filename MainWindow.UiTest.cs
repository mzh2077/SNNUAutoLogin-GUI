using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SNNUAutoLogin.Services;

namespace SNNUAutoLogin;

/// <summary>
/// 开发期界面自检（--ui-test）。
/// 校验下拉框与设置项的布局一致性、命中测试是否正确，并逐状态出图。
/// </summary>
public sealed partial class MainWindow
{
    private async Task RunUiSelfTestAsync()
    {
        var sb = new StringBuilder();

        await Task.Delay(1500);

        sb.AppendLine($"uiReady    = {_uiReady}   field={_operator}");
        sb.AppendLine($"theme      = dark={_theme.IsDark} accent=#{_theme.Accent.R:X2}{_theme.Accent.G:X2}{_theme.Accent.B:X2}");
        sb.AppendLine($"statusCard = {RectOf(StatusCard)}");
        sb.AppendLine($"accountCrd = {RectOf(AccountCard)}");
        sb.AppendLine($"behavior   = {RectOf(BehaviorCard)}");
        sb.AppendLine($"logCard    = {RectOf(LogCard)}");
        sb.AppendLine($"ringHost   = {RectOf(RingHost)}");
        sb.AppendLine($"opBox      = {RectOf(OperatorBox)}");
        sb.AppendLine($"ivBox      = {RectOf(IntervalBox)} val={IntervalBox.Value}");
        sb.AppendLine($"guardCard  = {RectOf(DeviceGuardCard)}");
        sb.AppendLine($"rulesCard  = {RectOf(RulesCard)}");
        sb.AppendLine($"guardState = {RectOf(DeviceGuardStateText)}");
        sb.AppendLine($"rulesLink  = {RectOf(RulesCurrentLinkText)}");
        sb.AppendLine($"accBox     = {RectOf(AccountBox)}");
        sb.AppendLine($"pwdBox     = {RectOf(PwdBox)}");

        // 下拉框逐项切换：字段值、下拉框选中项、持久化值三者必须一致
        var names = new[] { "campus", "mobile", "unicom", "telecom" };
        for (int i = 0; i < 4; i++)
        {
            SelectOperator((Operator)i);
            await Task.Delay(180);

            var item = OperatorBox.SelectedItem as ComboBoxItem;
            sb.AppendLine($"--- operator {names[i]} ---");
            sb.AppendLine($"  field    = {_operator} ({(int)_operator})");
            sb.AppendLine($"  selIndex = {OperatorBox.SelectedIndex}");
            sb.AppendLine($"  selText  = {item?.Content}");
            sb.AppendLine($"  hit      = {HitTest(OperatorBox)}");
            sb.AppendLine($"  width    = {OperatorBox.ActualWidth:F1}");
        }

        // 还原本机配置
        SelectOperator(_rt.Store.Settings.Operator);
        await Task.Delay(200);
        sb.AppendLine($"restored   = field={_operator} sel={OperatorBox.SelectedIndex}");

        // 设置项命中测试：开关与行标题不能互相遮挡
        sb.AppendLine($"hit.autoSw = {HitTest(AutoStartSwitch)}");
        sb.AppendLine($"hit.connBtn= {HitTest(ConnectBtn)}");

        sb.AppendLine($"sw.auto    = {RectOf(AutoStartSwitch)} min={AutoStartSwitch.MinWidth} max={AutoStartSwitch.MaxWidth} pad={AutoStartSwitch.Padding}");
        sb.AppendLine($"sw.parts   = {DescribeSwitch(AutoStartSwitch)}");
        sb.AppendLine($"sw.conn    = {RectOf(AutoReconnectSwitch)}");
        sb.AppendLine($"opBox.right= {RightOf(OperatorBox):F1}  sw.right= {RightOf(AutoStartSwitch):F1}");

        // 三种关键状态出图
        ApplyState(ConnState.Online, null, animate: false);
        await Task.Delay(400);
        await CaptureAsync();

        ApplyState(ConnState.Offline, null, animate: false);
        await Task.Delay(400);
        await SaveElementAsync(StatusCard, "snnu_state_offline.png");

        ApplyState(ConnState.Failed, null, animate: false);
        await Task.Delay(400);
        await SaveElementAsync(StatusCard, "snnu_state_failed.png");

        await SaveElementAsync(AccountCard, "snnu_account.png");
        await SaveElementAsync(BehaviorCard, "snnu_behavior.png");

        System.IO.File.WriteAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snnu_uitest.txt"),
            sb.ToString(), Encoding.UTF8);

        Application.Current.Exit();
    }

    private static double RightOf(FrameworkElement el)
    {
        try
        {
            var p = el.TransformToVisual(null).TransformPoint(
                new Windows.Foundation.Point(el.ActualWidth, 0));
            return p.X;
        }
        catch { return double.NaN; }
    }

    /// <summary>列出 ToggleSwitch 模板内部关键部件的位置与尺寸，用于定位裁切来源。</summary>
    private static string DescribeSwitch(UIElement root)
    {
        var names = new[]
        {
            "SwitchAreaGrid", "OuterBorder", "SwitchKnobBounds",
            "SwitchKnob", "SwitchKnobOn", "SwitchKnobOff",
            "OnContentPresenter", "OffContentPresenter", "HeaderContentPresenter"
        };

        var found = new System.Collections.Generic.List<string>();
        Walk(root, 0);

        return found.Count == 0 ? "none" : string.Join(" | ", found);

        void Walk(UIElement node, int depth)
        {
            if (depth > 6) return;
            int n = VisualTreeHelper.GetChildrenCount(node);
            for (int i = 0; i < n; i++)
            {
                var child = VisualTreeHelper.GetChild(node, i);
                if (child is FrameworkElement fe)
                {
                    string name = fe.Name;
                    if (Array.IndexOf(names, name) >= 0)
                    {
                        var r = fe.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
                        found.Add($"{name}({r.X:F1},{r.Y:F1} {fe.ActualWidth:F1}x{fe.ActualHeight:F1} vis={fe.Visibility})");
                    }
                }
                if (child is UIElement ue) Walk(ue, depth + 1);
            }
        }
    }

    /// <summary>命中测试：元素中心点最上层是哪个类型，以及是否命中自身。</summary>
    private string HitTest(FrameworkElement el)
    {
        try
        {
            var c = el.TransformToVisual(null).TransformPoint(
                new Windows.Foundation.Point(el.ActualWidth / 2, el.ActualHeight / 2));

            var list = VisualTreeHelper.FindElementsInHostCoordinates(
                new Windows.Foundation.Point(c.X, c.Y), Root);

            var top = list.FirstOrDefault();
            if (top is null) return "none";
            bool owns = false;
            UIElement? cur = top;
            while (cur is not null)
            {
                if (ReferenceEquals(cur, el)) { owns = true; break; }
                cur = VisualTreeHelper.GetParent(cur) as UIElement;
            }

            return $"{top.GetType().Name} {(owns ? "-> own" : "-> OTHER")}";
        }
        catch (Exception ex)
        {
            return "err:" + ex.GetType().Name;
        }
    }

    private static async Task SaveElementAsync(FrameworkElement el, string fileName)
    {
        try
        {
            var rtb = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await rtb.RenderAsync(el);

            var pixels = await rtb.GetPixelsAsync();
            var buffer = new byte[pixels.Length];
            using (var reader = Windows.Storage.Streams.DataReader.FromBuffer(pixels))
            {
                reader.ReadBytes(buffer);
            }

            using var mem = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(
                Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, mem);
            encoder.SetPixelData(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)rtb.PixelWidth, (uint)rtb.PixelHeight, 96, 96, buffer);
            await encoder.FlushAsync();

            var size = (uint)mem.Size;
            var png = new byte[size];
            using (var reader = new Windows.Storage.Streams.DataReader(mem.GetInputStreamAt(0)))
            {
                await reader.LoadAsync(size);
                reader.ReadBytes(png);
            }

            System.IO.File.WriteAllBytes(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), fileName), png);
        }
        catch { }
    }
}
