using System;
using System.Threading;
using System.Threading.Tasks;

namespace SNNUAutoLogin.Services;

/// <summary>
/// 门户客户端里「在线设备管理」用到的部分。
///
/// 抽成接口有两个原因：
///   1. 让 <see cref="DeviceManager"/> 能在单元测试里换成假的实现，不出网；
///   2. 明确划出「设备管理」与「登录/巡检」之间的边界 —— 换学校时
///      只需照着门户实际接口调整这一层。
/// </summary>
public interface IOnlineDevicePortal : IDisposable
{
    /// <summary>取账号下的在线设备列表。</summary>
    Task<DeviceListResult> GetOnlineDevicesAsync(int port, CancellationToken ct = default);

    /// <summary>
    /// 强制下线某个会话（踢别人的设备）。
    /// 走门户在线控制页自己的 type=dm 接口。
    /// </summary>
    Task<bool> ForceOfflineAsync(string frameUrl, string sessionId, CancellationToken ct = default);

    /// <summary>
    /// 断开本机当前这条连接。
    /// 对应门户 userstatus.jsp 底部那个「断开连接」按钮（logoff）。
    /// </summary>
    Task<bool> DisconnectAsync(int port, CancellationToken ct = default);
}
