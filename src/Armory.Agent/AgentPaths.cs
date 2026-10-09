using System.Security.Cryptography;
using System.Text;

namespace Armory.Agent;

// The per-user data folder, %LOCALAPPDATA%\IDEA Armory: settings.json, logs\, secrets\,
// incidents\ (docs/agent/TELEMETRY.md) and WebView2\. ARMORY_DATA_DIR replaces it for automated tests only, so a test instance never
// shares a sign-in, settings or the single-instance guard with a real one.
public sealed record AgentPaths(string DataFolder, bool IsOverridden)
{
    public const string DataFolderVariable = "ARMORY_DATA_DIR";
    public const string SiteVariable = "ARMORY_SITE_URL";
    public static readonly Uri DefaultSite = new("https://ideabosco.com");

    public string SettingsFile => Path.Combine(DataFolder, "settings.json");
    public string LogFolder => Path.Combine(DataFolder, "logs");
    public string LogFile => Path.Combine(LogFolder, "agent.log");
    public string CrashFile => Path.Combine(LogFolder, "crash.log");
    public string SecretsFolder => Path.Combine(DataFolder, "secrets");
    // The SolidWorks link's own file (docs/agent/SOLIDWORKS.md): the student's Save to Version
    // setting while Armory changed it, put back after a crash.
    public string SolidWorksFile => Path.Combine(DataFolder, "solidworks.json");
    public string IncidentsFolder => Path.Combine(DataFolder, "incidents");
    public string LastFlightFile => Path.Combine(IncidentsFolder, "last-flight.json.gz");
    public string WebView2Folder => Path.Combine(DataFolder, "WebView2");
    public static string AppFolder => AppContext.BaseDirectory;
    public static string WebRoot => Path.Combine(AppFolder, "wwwroot");

    public static AgentPaths Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(DataFolderVariable);
        if (!string.IsNullOrWhiteSpace(overridden) && Path.IsPathFullyQualified(overridden))
            return new(Path.GetFullPath(overridden), true);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return new(Path.Combine(local, "IDEA Armory"), false);
    }

    // A suffix that keeps a test instance's mutex and events apart from the real app's.
    public string InstanceSuffix => IsOverridden
        ? "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(DataFolder.ToUpperInvariant())))[..16]
        : string.Empty;

    // https://ideabosco.com unless ARMORY_SITE_URL names another absolute http(s) site (tests).
    public static Uri Site()
    {
        var overridden = Environment.GetEnvironmentVariable(SiteVariable);
        return !string.IsNullOrWhiteSpace(overridden) && Uri.TryCreate(overridden, UriKind.Absolute, out var site) &&
            (site.Scheme == Uri.UriSchemeHttps || site.Scheme == Uri.UriSchemeHttp) ? site : DefaultSite;
    }

    public static string Version { get; } = typeof(AgentPaths).Assembly
        .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
        .OfType<System.Reflection.AssemblyInformationalVersionAttribute>().FirstOrDefault()?.InformationalVersion.Split('+')[0] ?? "0.0.0";
}
