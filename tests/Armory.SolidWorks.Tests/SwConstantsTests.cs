namespace Armory.SolidWorks.Tests;

// The link's one table of SolidWorks numbers (SwConstants) against the values measured from
// the type information of SolidWorks 2025 SP5 and 2026 (research addin-registration.md section
// 5, identical in both). Calls go by name; these numbers are what events and the fake need.
public sealed class SwConstantsTests
{
    [Fact]
    public void The_interface_ids_are_the_measured_ones()
    {
        Assert.Equal(new Guid("83A33D22-27C5-11CE-BFD4-00400513BB57"), SwConstants.ISldWorks);
        Assert.Equal(new Guid("B90793FB-EF3D-4B80-A5C4-99959CDB6CEB"), SwConstants.IModelDoc2);
        Assert.Equal(new Guid("99F4D4AF-F268-4EE1-8C55-041F7BECF879"), SwConstants.IModelDocExtension);
        Assert.Equal(new Guid("DA306A0D-EAC5-4406-8610-B1DA805D9270"), SwConstants.ISwAddin);
        Assert.Equal(new Guid("83A33D22-37C5-11CE-BFD4-00400513BB57"), SwConstants.DSldWorksEvents);
        Assert.Equal(new Guid("83A33D32-37C5-11CE-BFD4-00400513BB57"), SwConstants.DPartDocEvents);
        Assert.Equal(new Guid("83A33D35-37C5-11CE-BFD4-00400513BB57"), SwConstants.DAssemblyDocEvents);
        Assert.Equal(new Guid("83A33D34-37C5-11CE-BFD4-00400513BB57"), SwConstants.DDrawingDocEvents);
        Assert.Equal(new Guid("00020400-0000-0000-C000-000000000046"), SwConstants.IDispatch);
    }

    [Fact]
    public void The_application_event_numbers_are_the_measured_ones()
    {
        Assert.Equal(
            [("DestroyNotify", 3), ("ActiveDocChangeNotify", 4), ("ActiveModelDocChangeNotify", 5), ("FileNewNotify2", 12), ("FileOpenNotify2", 13),
                ("FileOpenPostNotify", 22), ("CommandCloseNotify", 29), ("FileCloseNotify", 32)],
            new[]
            {
                ("DestroyNotify", SwConstants.DestroyNotify), ("ActiveDocChangeNotify", SwConstants.ActiveDocChangeNotify),
                ("ActiveModelDocChangeNotify", SwConstants.ActiveModelDocChangeNotify), ("FileNewNotify2", SwConstants.FileNewNotify2),
                ("FileOpenNotify2", SwConstants.FileOpenNotify2), ("FileOpenPostNotify", SwConstants.FileOpenPostNotify),
                ("CommandCloseNotify", SwConstants.CommandCloseNotify), ("FileCloseNotify", SwConstants.FileCloseNotify),
            });
    }

    // FileSaveNotify, FileSaveAsNotify2, FileSavePostNotify, ModifyNotify, DestroyNotify2 per
    // document type: the numbers differ between parts, assemblies and drawings.
    [Theory]
    [InlineData(1, "83A33D32-37C5-11CE-BFD4-00400513BB57", 6, 26, 31, 19, 50)]
    [InlineData(2, "83A33D35-37C5-11CE-BFD4-00400513BB57", 6, 31, 39, 21, 62)]
    [InlineData(3, "83A33D34-37C5-11CE-BFD4-00400513BB57", 6, 23, 26, 18, 42)]
    public void The_document_event_numbers_are_the_measured_ones(int docType, string iid, int save, int saveAs, int savePost, int modify, int destroy2)
        => Assert.Equal(new DocumentEvents(docType, new Guid(iid), save, saveAs, savePost, modify, destroy2), SwConstants.EventsFor(docType));

    [Fact]
    public void Only_parts_assemblies_and_drawings_have_document_events()
    {
        Assert.Null(SwConstants.EventsFor(0));
        Assert.Null(SwConstants.EventsFor(4));
        Assert.Equal((1, 2, 3), (SwDocTypes.Part, SwDocTypes.Assembly, SwDocTypes.Drawing));
    }

    [Fact]
    public void The_member_numbers_are_the_measured_ones()
    {
        Assert.Equal(new Dictionary<string, int>
        {
            ["ActiveDoc"] = 1, ["Frame"] = 5, ["ExitApp"] = 6, ["CloseDoc"] = 7, ["RevisionNumber"] = 12, ["SendMsgToUser2"] = 76, ["LoadAddIn"] = 78,
            ["VersionHistory"] = 81, ["GetOpenDocumentByName"] = 122, ["GetProcessID"] = 166, ["OpenDoc6"] = 167, ["CommandInProgress"] = 228,
            ["GetDocuments"] = 273, ["ActivateDoc3"] = 306, ["SetAddinCallbackInfo2"] = 310, ["StartupProcessCompleted"] = 311,
        }, SwConstants.SldWorksMembers);
        Assert.Equal(new Dictionary<string, int>
        {
            ["GetTitle"] = 65607, ["GetPathName"] = 65608, ["GetType"] = 65609, ["IsOpenedReadOnly"] = 65914, ["VersionHistory"] = 65922,
            ["SetReadOnlyState"] = 65938, ["GetSaveFlag"] = 65986, ["ReloadOrReplace"] = 66289, ["Extension"] = 66306,
        }, SwConstants.ModelDoc2Members);
        Assert.Equal(new Dictionary<string, int> { ["GetAdvancedSaveAsOptions"] = 314, ["SaveAs3"] = 315 }, SwConstants.ModelDocExtensionMembers);
        Assert.Equal((10, 10), (SwConstants.FrameSetStatusBarText, SwConstants.AdvancedSaveAsPreviousVersion));
    }

    [Fact]
    public void The_enum_numbers_are_the_published_ones()
    {
        Assert.Equal((1, 2, 3, 4), (SwConstants.SaveTypeSave, SwConstants.SaveTypeSaveAs, SwConstants.SaveTypeSaveAsCopy, SwConstants.SaveTypeSaveAsCopyAndOpen));
        Assert.Equal((0, 1), (SwConstants.DestroyTypeDestroy, SwConstants.DestroyTypeHidden));
        Assert.Equal((1, 2, 1, 2), (SwConstants.OpenSilent, SwConstants.OpenReadOnly, SwConstants.SaveAsSilent, SwConstants.SaveAsCopy));
        Assert.Equal((0, 1, 2), (SwConstants.SaveToVersionDoNotUpgrade, SwConstants.SaveToVersionPenultimate, SwConstants.SaveToVersionAntepenultimate));
        Assert.Equal((0, 1, 2), (SwConstants.CompatibilityAlwaysShow, SwConstants.CompatibilityShowOnIncompatible, SwConstants.CompatibilityNeverShow));
        Assert.Equal((0, 1, 2, 3), (SwConstants.CompatibilityCompleted, SwConstants.CompatibilityFailUnknown, SwConstants.CompatibilityFailFileNotResolved,
            SwConstants.CompatibilityFailInvalidVersion));
        Assert.Equal((0, 3, 16), (SwConstants.ReloadOkay, SwConstants.ReloadModifiedNotReloaded, SwConstants.ReloadReadOnlyChanged));
        Assert.Equal("SolidWorks_PID_4120", SwConstants.RotName(4120));
        Assert.Equal(("SldWorks.Application", "SLDWORKS"), (SwConstants.ProgId, SwConstants.ProcessName));
    }

    // swSaveToVersion_e is relative to the running release (1: one back): what Core's plan says.
    [Fact]
    public void Core_save_down_values_are_swSaveToVersion_e()
    {
        Assert.Equal(SwConstants.SaveToVersionPenultimate, Armory.Core.SaveDown.SaveToVersionValue(Armory.Core.SaveDownPlan.Penultimate));
        Assert.Equal(SwConstants.SaveToVersionAntepenultimate, Armory.Core.SaveDown.SaveToVersionValue(Armory.Core.SaveDownPlan.Antepenultimate));
    }
}
