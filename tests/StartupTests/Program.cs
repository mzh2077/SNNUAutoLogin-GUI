using Microsoft.UI.Dispatching;
using Microsoft.Win32;
using SNNUAutoLogin.Services;

int checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception(name);
    Console.WriteLine("PASS " + name);
    checks++;
}

string path = @"C:\Portable App\SNNUAutoLogin.exe";
void CheckRegistration(Func<bool, string, bool> setEnabled, Func<string, bool> isEnabled,
    Func<string?> readEntry, Action<string> writeLegacyEntry)
{
    Check(setEnabled(false, path), "disable absent entry");
    Check(setEnabled(true, path), "register absent entry");
    Check(readEntry() == $"\"{path}\" --tray", "quoted path and tray argument");
    string moved = @"C:\Moved App\SNNUAutoLogin.exe";
    Check(!isEnabled(moved), "same filename at old path is invalid");
    Check(setEnabled(true, moved), "repair moved portable path");
    Check(!isEnabled(path), "old path no longer matches");
    writeLegacyEntry($"\"{moved}\"");
    Check(!isEnabled(moved), "missing tray argument is invalid");
    Check(setEnabled(true, moved), "repair legacy command");
    Check(!setEnabled(true, ""), "empty path rejected");
    Check(setEnabled(false, moved) && readEntry() is null, "disable removes entry");
}

string? entry = null;
CheckRegistration(
    (enabled, executable) => StartupManager.SetEnabled(enabled, executable, () => entry, value => entry = value),
    executable => StartupManager.IsEnabled(executable, () => entry),
    () => entry,
    value => entry = value);

Check(!StartupManager.SetEnabled(true, path, () => entry, _ => throw new UnauthorizedAccessException()),
    "registry write denial is reported");
Check(!StartupManager.SetEnabled(false, path, () => throw new UnauthorizedAccessException(), _ => { }),
    "registry read denial does not confirm removal");
Check(!StartupManager.SetEnabled(true, path, () => entry, _ => { }),
    "registration must be verified after writing");

if (args.Contains("--registry-integration") && !args.Contains("--skip-registry"))
{
    // Use a unique test key and leave the user's Run key untouched.
    string runKey = @"Software\SNNUAutoLogin.Tests\" + Guid.NewGuid().ToString("N");
    Console.WriteLine("Windows registry integration");
    try
    {
        CheckRegistration(
            (enabled, executable) => StartupManager.SetEnabled(enabled, executable, runKey),
            executable => StartupManager.IsEnabled(executable, runKey),
            () =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(runKey);
                return key?.GetValue("SNNUAutoLogin") as string;
            },
            value =>
            {
                using var key = Registry.CurrentUser.OpenSubKey(runKey, true);
                key!.SetValue("SNNUAutoLogin", value);
            });
    }
    finally
    {
        Registry.CurrentUser.DeleteSubKeyTree(runKey, false);
    }
}

async Task Scenario(string name, bool startup, Action<CredentialStore>? configure,
    LoginResult status, LoginResult login, params int[] expectedCalls)
{
    var store = new CredentialStore();
    configure?.Invoke(store);
    PortalClient.Status = status;
    PortalClient.Login = login;
    PortalClient.LoginCalls = 0;
    using var coordinator = new LoginCoordinator(store, new DispatcherQueue(), startup);
    foreach (int expected in expectedCalls)
    {
        await coordinator.RefreshAsync();
        Check(PortalClient.LoginCalls == expected, name + " tick " + checks);
    }
}

var offline = LoginResult.Fail("offline");
var ok = new LoginResult { Success = true };
await Scenario("startup logs in once with reconnect off", true, null, offline, ok, 1, 1);
await Scenario("manual launch respects reconnect off", false, null, offline, ok, 0);
await Scenario("disabled startup does not log in", true, s => s.Settings.AutoStart = false, offline, ok, 0);
await Scenario("reconnect remains active", true, s => s.Settings.AutoReconnect = true, offline, ok, 1, 2);
await Scenario("failed startup retries", true, null, offline, LoginResult.Fail("rejected"), 1, 2);
await Scenario("missing password", true, s => s.Password = "", offline, ok, 0);
await Scenario("missing account", true, s => s.Settings.Account = " ", offline, ok, 0);

var pendingStore = new CredentialStore();
using (var coordinator = new LoginCoordinator(pendingStore, new DispatcherQueue(), true))
{
    PortalClient.LoginCalls = 0;
    PortalClient.Status = LoginResult.Fail("Portal unreachable");
    await coordinator.RefreshAsync();
    Check(PortalClient.LoginCalls == 0, "wait for network without submitting credentials");
    PortalClient.Status = offline;
    PortalClient.Login = ok;
    coordinator.SuppressAuto(true);
    await coordinator.RefreshAsync();
    Check(PortalClient.LoginCalls == 0, "suppression applies to startup login");
    coordinator.SuppressAuto(false);
    await coordinator.RefreshAsync();
    Check(PortalClient.LoginCalls == 1, "startup login survives network delay");
    await coordinator.RefreshAsync();
    Check(PortalClient.LoginCalls == 1, "no reconnect after startup success");
}

using (var coordinator = new LoginCoordinator(new CredentialStore(), new DispatcherQueue(), true))
{
    PortalClient.LoginCalls = 0;
    PortalClient.Status = ok;
    await coordinator.RefreshAsync();
    PortalClient.Status = offline;
    await coordinator.RefreshAsync();
    Check(PortalClient.LoginCalls == 0, "already online completes startup login");
}
var switchStore = new CredentialStore();
switchStore.Settings.Account = "test@mobile";
using (var coordinator = new LoginCoordinator(switchStore, new DispatcherQueue()))
{
    PortalClient.LoginCalls = 0;
    PortalClient.Login = ok;
    PortalClient.DisconnectSucceeds = false;
    await coordinator.ManualConnectAsync(Operator.Telecom);
    Check(PortalClient.LoginCalls == 0, "failed disconnect does not attempt outlet login");
    PortalClient.DisconnectSucceeds = true;
    await coordinator.ManualConnectAsync(Operator.Telecom);
    Check(PortalClient.LastOperator == Operator.Telecom && PortalClient.LastAccount == "test" && PortalClient.LastForce,
        "explicit outlet overrides the account suffix and forces login");
    await coordinator.ManualConnectAsync();
    Check(PortalClient.LastOperator == Operator.Telecom, "subsequent login keeps the temporary outlet");
    int previousDisconnects = PortalClient.DisconnectCalls;
    await coordinator.ManualConnectAsync(reconnect: true);
    Check(PortalClient.DisconnectCallsAtLogin == previousDisconnects + 1 && PortalClient.LastForce
        && PortalClient.LastOperator == Operator.Telecom, "reconnect disconnects before forcing login on the current outlet");
}
Console.WriteLine($"{checks} checks passed.");
