namespace Armory.SolidWorks.Tests;

// The two Save to Version preference numbers: read from the installed SolidWorks' interop
// assembly as metadata (never loaded as code), or from the settings a lab step wrote; never a
// default. LinkSettings keeps the student's own setting across a crash.
public sealed class SaveToVersionEnumsTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "armory-sw-settings-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    // This test assembly defines enums with the interop's namespace and names (below), the way
    // SolidWorks.Interop.swconst.dll of 2026 SP3 does: their constants are what the reader finds.
    [Fact]
    public void The_numbers_are_read_from_an_assemblys_metadata()
    {
        var found = SaveToVersionEnums.FromAssembly(typeof(SaveToVersionEnumsTests).Assembly.Location);
        Assert.Equal(new SaveToVersionIds(912, 913), found);
    }

    [Fact]
    public void An_assembly_without_them_or_no_file_gives_none()
    {
        Assert.Null(SaveToVersionEnums.FromAssembly(typeof(SaveToVersionEnums).Assembly.Location));
        Assert.Null(SaveToVersionEnums.FromAssembly(Path.Combine(folder, "missing.dll")));
        Assert.Null(SaveToVersionEnums.Resolve(LinkSettings.Load(null), null, null));
        Assert.Equal(Path.Combine(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS", "api", "redist", "SolidWorks.Interop.swconst.dll"),
            SaveToVersionEnums.InteropPath(@"C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS"));
    }

    // SLDWORKS.exe's folder holds api\redist\SolidWorks.Interop.swconst.dll; settings win over it.
    [Fact]
    public void The_installed_interop_is_found_beside_SolidWorks_and_settings_win()
    {
        var install = Path.Combine(folder, "SOLIDWORKS");
        Directory.CreateDirectory(Path.Combine(install, "api", "redist"));
        File.Copy(typeof(SaveToVersionEnumsTests).Assembly.Location, Path.Combine(install, "api", "redist", "SolidWorks.Interop.swconst.dll"));
        var settings = LinkSettings.Load(Path.Combine(folder, "solidworks.json"));
        Assert.Equal(new SaveToVersionIds(912, 913), SaveToVersionEnums.Resolve(settings, Path.Combine(install, "SLDWORKS.exe"), null));
        settings.SetIds(new SaveToVersionIds(1, 2));
        Assert.Equal(new SaveToVersionIds(1, 2), SaveToVersionEnums.Resolve(LinkSettings.Load(Path.Combine(folder, "solidworks.json")), Path.Combine(install, "SLDWORKS.exe"), null));
    }

    [Fact]
    public void The_students_setting_survives_a_restart()
    {
        var file = Path.Combine(folder, "solidworks.json");
        var settings = LinkSettings.Load(file);
        Assert.Null(settings.Student(34));
        Assert.Null(settings.SaveToVersionIds);
        settings.SetStudent(34, new LinkSettings.StudentSetting(true, 2, Changed: true));
        settings.SetStudent(35, new LinkSettings.StudentSetting(false, 0, Changed: false));
        var again = LinkSettings.Load(file);
        Assert.Equal(new LinkSettings.StudentSetting(true, 2, true), again.Student(34));
        Assert.Equal(new LinkSettings.StudentSetting(false, 0, false), again.Student(35));
        Assert.False(File.Exists(file + ".pending"));
        // A damaged file reads as nothing kept, never an exception.
        File.WriteAllText(file, "{ not json");
        Assert.Null(LinkSettings.Load(file).Student(34));
    }
}
