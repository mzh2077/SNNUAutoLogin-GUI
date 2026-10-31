using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;

namespace SNNUAutoLogin.Services;

/// <summary>门户接入端点（主机 + 端口）。不同端口对应不同接入点，设备列表按账号统计。</summary>
public sealed record PortalEndpoint(string Host, int Port)
{
    public string Base => $"http://{Host}:{Port}/snnuportal/";

    public override string ToString() => $"{Host}:{Port}";
}

/// <summary>账号下的一台在线设备。</summary>
public sealed class OnlineDevice : INotifyPropertyChanged
{
    public string Ip { get; init; } = "";
    public string Mac { get; init; } = "";
    public string LoginTime { get; init; } = "";
    public string DeviceName { get; init; } = "";
    public string Location { get; init; } = "";

    /// <summary>会话标识。为空表示这台是当前出口、只能断开而不能强制下线。</summary>
    public string? SessionId { get; init; }

    /// <summary>是否就是本机当前这条连接。</summary>
    public bool IsCurrent { get; init; }

    /// <summary>能看到这台设备的门户接入端口（同一账号可能在多个接入点上同时在线）。</summary>
    public List<int> Ports { get; init; } = new();

    /// <summary>能否强制下线：只有别人（或别的出口）的会话才有会话标识。</summary>
    public bool CanForceOffline => !string.IsNullOrEmpty(SessionId);

    /// <summary>第二行的明细：MAC、接入端口、位置。</summary>
    public string Detail
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(Mac)) parts.Add(Mac);
            if (Ports.Count > 0)
                parts.Add("端口 " + string.Join("/", Ports.Select(p => p.ToString())));
            else if (!string.IsNullOrWhiteSpace(Location))
                parts.Add(Location);

            return string.Join(" · ", parts);
        }
    }

    /// <summary>设备名；门户没填时退回 MAC，再不行就叫「未知设备」。</summary>
    public string DisplayName =>
        !string.IsNullOrWhiteSpace(DeviceName) ? DeviceName
        : !string.IsNullOrWhiteSpace(Mac) ? Mac
        : "未知设备";

    private string _status = "在线";

    /// <summary>在线 / 下线中 / 已断开。改动会通知界面刷新。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>在线设备列表的查询结果。</summary>
public sealed class DeviceListResult
{
    public bool Success { get; init; }
    public string Message { get; init; } = "";
    public IReadOnlyList<OnlineDevice> Devices { get; init; } = Array.Empty<OnlineDevice>();

    /// <summary>在线控制页地址；强制下线要带上它的查询参数。</summary>
    public string? FrameUrl { get; init; }

    public static DeviceListResult Ok(IReadOnlyList<OnlineDevice> devices, string? frameUrl) =>
        new() { Success = true, Devices = devices, FrameUrl = frameUrl };

    public static DeviceListResult Fail(string msg) => new() { Success = false, Message = msg };
}

/// <summary>
/// 在线控制页（设备表格）的解析。
///
/// 门户的 userstatus.jsp 里嵌了一个 iframe，设备表格在 iframe 里：
///   &lt;iframe src="http://{host}:80/zili/auth_my_state.php?vloginid=…&amp;ts=…&amp;id=…"&gt;
/// 表格每行五列：上网IP / MAC地址 / 上网时间 / 上网设备 / 上网位置，
/// 第六列是操作位 —— 别人的会话带一个「强制下线」按钮，onclick 里写着会话标识：
///   onclick="dm('YT-ME6001201050000000ba052aAAANNG')"
/// 本机当前这条连接没有会话标识，只能断开。
///
/// 这里是纯函数，不碰网络，方便单测，也方便换学校时改。
/// </summary>
public static class DeviceListParser
{
    private static readonly Regex BodyRegex = new(
        @"<tbody[^>]*\bid\s*=\s*[""']ec_table_body[""'][^>]*>([\s\S]*?)</tbody>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex RowRegex = new(
        @"<tr[^>]*>([\s\S]*?)</tr>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex CellRegex = new(
        @"<td[^>]*>([\s\S]*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex SessionRegex = new(
        @"dm\(\s*[""']([^""']+)[""']\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ScriptRegex = new(
        @"<script[\s\S]*?</script>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>解析在线控制页，取出账号下的在线设备。</summary>
    /// <param name="html">iframe 页面的 HTML（UTF-8）。</param>
    /// <param name="currentIp">本机当前出口 IP；与它相同的行标记为「当前设备」。</param>
    public static List<OnlineDevice> Parse(string html, string? currentIp)
    {
        var list = new List<OnlineDevice>();
        if (string.IsNullOrEmpty(html)) return list;

        var body = BodyRegex.Match(html);
        if (!body.Success) return list;

        foreach (Match row in RowRegex.Matches(body.Groups[1].Value))
        {
            var cells = CellRegex.Matches(row.Groups[1].Value);
            if (cells.Count < 5) continue;

            string[] values = new string[cells.Count];
            for (int i = 0; i < cells.Count; i++)
                values[i] = CleanCell(cells[i].Groups[1].Value);

            var ip = values[0];
            if (string.IsNullOrWhiteSpace(ip)) continue;

            // 会话标识藏在操作列的 onclick 里
            string? sessionId = null;
            var dm = SessionRegex.Match(cells[^1].Value);
            if (dm.Success) sessionId = dm.Groups[1].Value;

            list.Add(new OnlineDevice
            {
                Ip = ip,
                Mac = values[1],
                LoginTime = values[2],
                DeviceName = values[3],
                Location = values[4],
                SessionId = sessionId,
                IsCurrent = !string.IsNullOrEmpty(currentIp) && ip == currentIp
            });
        }

        return list;
    }

    /// <summary>把单元格 HTML 洗成纯文本。</summary>
    private static string CleanCell(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";

        var text = ScriptRegex.Replace(html, " ");
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = text.Replace("&nbsp;", " ", StringComparison.OrdinalIgnoreCase);
        text = System.Net.WebUtility.HtmlDecode(text);

        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>
    /// 拼出强制下线的地址：在在线控制页地址后面加 type=dm 与会话标识，
    /// 并保留原有的 vloginid / ts / id 参数（portal 要求这几个参数齐全）。
    ///
    /// 用字符串切分而不是 Uri 重组：重组会把显式写出的默认端口（:80）抹掉，
    /// 而门户页面自己的 JS 用的是相对路径，行为要跟它一致。
    /// </summary>
    public static string BuildForceOfflineUrl(string frameUrl, string sessionId)
    {
        int q = frameUrl.IndexOf('?');
        string path = q >= 0 ? frameUrl[..q] : frameUrl;
        string query = q >= 0 ? frameUrl[(q + 1)..] : "";

        var sb = new System.Text.StringBuilder();
        sb.Append(path);
        sb.Append("?type=dm&sessionid=").Append(Uri.EscapeDataString(sessionId));
        if (query.Length > 0) sb.Append('&').Append(query);

        return sb.ToString();
    }

    /// <summary>
    /// 拼出「断开连接」的地址。
    ///
    /// 门户的 userstatus.jsp 底部那个大按钮就是它：
    ///   &lt;a href="logoff"&gt;&lt;img src="image/duankai.gif" …&gt;&lt;/a&gt;
    /// 断开的是**本机当前这条会话**，不是设备表格里的任意一行。
    /// </summary>
    public static string BuildDisconnectUrl(string portalRoot) =>
        portalRoot.TrimEnd('/') + "/logoff";
}
