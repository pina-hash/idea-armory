namespace Armory.Agent.Tests;

// Decision D14 as a plain rule, so it runs on every host (the Launch tests themselves need NTFS).
public sealed class LaunchPolicyTests
{
    [Theory]
    [InlineData("tool.exe")][InlineData("TOOL.EXE")][InlineData("a.com")][InlineData("a.bat")][InlineData("a.cmd")][InlineData("a.ps1")]
    [InlineData("a.psm1")][InlineData("a.vbs")][InlineData("a.vbe")][InlineData("a.js")][InlineData("a.jse")][InlineData("a.wsf")]
    [InlineData("a.wsh")][InlineData("a.hta")][InlineData("a.msi")][InlineData("a.msp")][InlineData("a.scr")][InlineData("a.lnk")]
    [InlineData("a.url")][InlineData("a.reg")][InlineData("a.cpl")][InlineData("a.jar")][InlineData("a.appref-ms")]
    [InlineData("Plate.SLDPRT.exe")]
    // Types ShellExecute also runs without PATHEXT naming them (the review's extension of D14).
    [InlineData("a.pif")][InlineData("a.scf")][InlineData("a.settingcontent-ms")][InlineData("a.application")][InlineData("a.appinstaller")]
    [InlineData("a.msix")][InlineData("a.appx")][InlineData("a.chm")][InlineData("a.xll")][InlineData("a.wsc")][InlineData("a.sct")]
    [InlineData("a.diagcab")][InlineData("a.py")][InlineData("a.PYW")][InlineData("a.sh")][InlineData("a.msc")][InlineData("a.theme")]
    public void Programs_scripts_and_shortcuts_are_never_opened(string name) => Assert.True(LaunchPolicy.IsRefused(name, null));

    [Theory]
    [InlineData("Plate.SLDPRT")][InlineData("Gearbox.SLDASM")][InlineData("Base.SLDDRW")][InlineData("notes.txt")][InlineData("drawing.pdf")]
    [InlineData("exe.SLDPRT")][InlineData("README")]
    [InlineData("Gearbox.STEP")][InlineData("Gearbox.stp")][InlineData("Plate.IGS")][InlineData("Plate.stl")][InlineData("Plate.dxf")]
    [InlineData("Plate.DWG")][InlineData("Plate.x_t")][InlineData("photo.png")][InlineData("BOM.xlsx")][InlineData("Notes.docx")]
    public void Documents_open_in_their_program(string name) => Assert.False(LaunchPolicy.IsRefused(name, null));

    [Fact]
    public void Every_type_this_computer_runs_is_refused_too()
    {
        Assert.True(LaunchPolicy.IsRefused("console.msc", ".COM;.EXE;.MSC; .PY ;"));
        Assert.True(LaunchPolicy.IsRefused("script.py", ".COM;.EXE;.MSC; .PY ;"));
        Assert.False(LaunchPolicy.IsRefused("Plate.SLDPRT", ".COM;.EXE;.MSC; .PY ;"));
    }
}
