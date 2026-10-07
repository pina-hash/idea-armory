using System.Text.RegularExpressions;

namespace Armory.Agent;

// Cache busting for the window's page. The WebView2 profile (%LOCALAPPDATA%\IDEA Armory\WebView2)
// survives an upgrade, so a new version must never be shown an old page from its cache: the
// start page and every same-folder script and style sheet it loads carry ?v=<version>, and
// MainWindow serves index.html itself with no-store (falling back to the folder mapping).
internal static partial class PageAssets
{
    internal const string HostName = "armory.local";

    internal static Uri StartPage(string version) => new("https://" + HostName + "/index.html?v=" + Uri.EscapeDataString(version));

    // src="app.js" and href="app.css" (relative, no query yet) become src="app.js?v=0.2.0".
    // Absolute URLs, data: URIs, anchors and anything already versioned are left alone.
    internal static string Versioned(string html, string version)
    {
        ArgumentNullException.ThrowIfNull(html);
        var tag = "?v=" + Uri.EscapeDataString(version);
        return Reference().Replace(html, match => match.Groups["attr"].Value + "=\"" + match.Groups["url"].Value + tag + "\"");
    }

    [GeneratedRegex(@"(?<attr>\b(?:src|href))=""(?<url>(?![A-Za-z][A-Za-z0-9+.-]*:)(?!//)[^""?#:]+\.(?:js|css))""", RegexOptions.CultureInvariant)]
    private static partial Regex Reference();
}
