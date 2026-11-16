// 凭据测试只需要门户端口表，不联网。
namespace SNNUAutoLogin.Services
{
    public sealed class PortalClient
    {
        public static readonly int[] KnownPorts = { 8603, 8601, 8602, 8605, 8609, 8604, 8606, 8607, 8608, 8610, 8600 };

        public static bool IsKnownPort(int port) => Array.IndexOf(KnownPorts, port) >= 0;
    }
}
