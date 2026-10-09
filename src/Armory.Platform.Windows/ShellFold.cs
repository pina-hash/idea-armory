using System.Runtime.InteropServices;

namespace Armory.Platform.Windows;

// The case folding of the badge table (docs/agent/EXPLORER.md): one UTF-16 unit to one, through
// the same LCMapStringEx(LOCALE_NAME_INVARIANT, LCMAP_UPPERCASE) call ArmoryBadges.dll makes
// inside Explorer, so Armory's keys and Explorer's paths fold alike. Pass Fold to
// Armory.Core.BadgeTable.Build; Core folds ASCII itself and asks this for the rest.
public static class ShellFold
{
    private const uint MapUppercase = 0x00000200;
    // Each unit's fold once asked; 0 means not asked yet (NUL is ASCII and never cached).
    private static readonly char[] Known = new char[char.MaxValue + 1];

    public static char Fold(char c)
    {
        if (c < 0x80) return c is >= 'a' and <= 'z' ? (char)(c - 32) : c;
        var known = Known[c];
        if (known != 0) return known;
        var folded = OperatingSystem.IsWindows() ? Map(c) : char.ToUpperInvariant(c);
        Known[c] = folded;
        return folded;
    }

    private static char Map(char c)
    {
        char[] source = [c];
        var destination = new char[1];
        return LCMapStringEx(string.Empty, MapUppercase, source, 1, destination, 1, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero) == 1 ? destination[0] : c;
    }

    // LOCALE_NAME_INVARIANT is the empty string.
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int LCMapStringEx(string locale, uint flags, char[] source, int sourceLength, [Out] char[] destination, int destinationLength,
        IntPtr versionInformation, IntPtr reserved, IntPtr sortHandle);
}
