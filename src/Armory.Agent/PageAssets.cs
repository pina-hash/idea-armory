using System.Text.RegularExpressions;

namespace Armory.Agent;

// Cache busting for the window's page. The WebView2 profile (%LOCALAPPDATA%\IDEA Armory\WebView2)
// survives an upgrade, so a new version must never be shown an old page from its cache: the
// start page and every same-folder script and style sheet it loads carry ?v=<version>, and
// MainWindow serves index.html itself with no-store (falling back to the folder mapping).
internal static partial class PageAssets
{
    internal const string HostName = "armory.local";

    // 0.5.4: the pictures the host makes in memory (a file's thumbnail, Send feedback's picture of
    // the window) are served from a host of their own. WebView2 never raises WebResourceRequested
    // for a host name mapped to a folder (SetVirtualHostNameToFolderMapping, Microsoft's "Custom
    // management of network requests"), so every /thumb/ and /shot/ ask on armory.local went to
    // the wwwroot folder and came back 404: no thumbnail ever showed (feedback 4d26aaf3,
    // 417e99fc) and nothing was logged. This name is mapped to nothing, so only MainWindow answers
    // it; the page's Content-Security-Policy lets its images in (index.html, img-src).
    internal const string PictureHost = "armory-pictures.local";
    internal const string PictureOrigin = "https://" + PictureHost;
    internal const string ThumbPrefix = "/thumb/";

    internal static Uri StartPage(string version) => new("https://" + HostName + "/index.html?v=" + Uri.EscapeDataString(version));

    // An address on the pictures host (https, its default port), parsed; else null.
    internal static Uri? PictureUri(string? uri)
        => Uri.TryCreate(uri, UriKind.Absolute, out var parsed) && parsed.Scheme == Uri.UriSchemeHttps && parsed.IsDefaultPort &&
           string.Equals(parsed.Host, PictureHost, StringComparison.OrdinalIgnoreCase) ? parsed : null;

    // The vault path a thumbnail's address names (app.js thumbAddress: each segment of the path
    // encodeURIComponent'd, then ?v=<version>), decoded; null for any other address. The path is
    // checked against the vault by WindowsVaultFileSystem.ExistingFile, never here.
    internal static string? ThumbVaultPath(string? uri)
    {
        if (PictureUri(uri) is not { } parsed) return null;
        // AbsolutePath keeps the page's escapes (%20, %23, %25 ...); each is undone exactly once.
        var path = parsed.AbsolutePath;
        if (!path.StartsWith(ThumbPrefix, StringComparison.Ordinal) || path.Length == ThumbPrefix.Length) return null;
        try { return Uri.UnescapeDataString(path[ThumbPrefix.Length..]); }
        catch (Exception error) when (error is ArgumentException or UriFormatException) { return null; }
    }

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
