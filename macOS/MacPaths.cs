namespace SNNUAutoLogin.Mac;

internal static class MacPaths
{
    public static string ConfigDirectory => Environment.GetEnvironmentVariable("SNNU_MAC_TEST_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "SNNUAutoLogin");
}
