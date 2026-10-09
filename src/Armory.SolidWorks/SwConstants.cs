namespace Armory.SolidWorks;

// Every interface id, event dispatch id and enum number the link uses, in one place
// (docs/agent/SOLIDWORKS.md, "The numbers"). Read from the type information of SolidWorks 2025
// SP5 and 2026 SP0 (identical in both) for the research behind the link; no Dassault assembly is
// part of the build. SwConstantsTests holds this table to those measured values, so a typo shows.
// Calls go by member name (IDispatch.GetIDsOfNames), so only events need the numbers.
internal static class SwConstants
{
    // ---- Interfaces -------------------------------------------------------------------------

    public static readonly Guid ISldWorks = new("83A33D22-27C5-11CE-BFD4-00400513BB57");
    public static readonly Guid IModelDoc2 = new("B90793FB-EF3D-4B80-A5C4-99959CDB6CEB");
    public static readonly Guid IModelDocExtension = new("99F4D4AF-F268-4EE1-8C55-041F7BECF879");
    // The in-process add-in interface (swpublished): only the fallback add-in would use it.
    public static readonly Guid ISwAddin = new("DA306A0D-EAC5-4406-8610-B1DA805D9270");
    // Event dispinterfaces.
    public static readonly Guid DSldWorksEvents = new("83A33D22-37C5-11CE-BFD4-00400513BB57");
    public static readonly Guid DPartDocEvents = new("83A33D32-37C5-11CE-BFD4-00400513BB57");
    public static readonly Guid DAssemblyDocEvents = new("83A33D35-37C5-11CE-BFD4-00400513BB57");
    public static readonly Guid DDrawingDocEvents = new("83A33D34-37C5-11CE-BFD4-00400513BB57");
    // IDispatch, which the events arrive on.
    public static readonly Guid IDispatch = new("00020400-0000-0000-C000-000000000046");

    // ---- DSldWorksEvents DISPIDs ------------------------------------------------------------

    public const int DestroyNotify = 3;               // ()
    public const int ActiveDocChangeNotify = 4;       // ()
    public const int ActiveModelDocChangeNotify = 5;  // ()
    public const int FileNewNotify2 = 12;             // (IDispatch NewDoc, int DocType, BSTR TemplateName)
    public const int FileOpenNotify2 = 13;            // (BSTR FileName)
    public const int FileOpenPostNotify = 22;         // (BSTR FileName)
    public const int CommandCloseNotify = 29;         // (int Command, int reason)
    public const int FileCloseNotify = 32;            // (BSTR FileName, int reason)

    // ---- Document events, per document type -----------------------------------------------------

    // FileSaveNotify (BSTR FileName), FileSaveAsNotify2 (BSTR FileName), FileSavePostNotify
    // (int saveType, BSTR FileName), ModifyNotify (), DestroyNotify2 (int DestroyType). Every
    // event returns int (0: carry on). FileSavePostCancelNotify () is not in the measured table:
    // the link looks its DISPID up by name in SolidWorks' own type information at attach, and
    // without it a canceled save simply ends the engine's hold after two minutes.
    public static readonly DocumentEvents Part = new(SwDocTypes.Part, DPartDocEvents, FileSaveNotify: 6, FileSaveAsNotify2: 26, FileSavePostNotify: 31, ModifyNotify: 19, DestroyNotify2: 50);
    public static readonly DocumentEvents Assembly = new(SwDocTypes.Assembly, DAssemblyDocEvents, FileSaveNotify: 6, FileSaveAsNotify2: 31, FileSavePostNotify: 39, ModifyNotify: 21, DestroyNotify2: 62);
    public static readonly DocumentEvents Drawing = new(SwDocTypes.Drawing, DDrawingDocEvents, FileSaveNotify: 6, FileSaveAsNotify2: 23, FileSavePostNotify: 26, ModifyNotify: 18, DestroyNotify2: 42);
    public const string FileSavePostCancelNotify = "FileSavePostCancelNotify";

    public static DocumentEvents? EventsFor(int docType) => docType switch
    {
        SwDocTypes.Part => Part,
        SwDocTypes.Assembly => Assembly,
        SwDocTypes.Drawing => Drawing,
        _ => null,
    };

    // ---- Member DISPIDs (for the fake SolidWorks and the logs; the link calls by name) ----------

    public static readonly IReadOnlyDictionary<string, int> SldWorksMembers = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["ActiveDoc"] = 1, ["Frame"] = 5, ["ExitApp"] = 6, ["CloseDoc"] = 7, ["RevisionNumber"] = 12, ["SendMsgToUser2"] = 76, ["LoadAddIn"] = 78,
        ["VersionHistory"] = 81, ["GetOpenDocumentByName"] = 122, ["GetProcessID"] = 166, ["OpenDoc6"] = 167, ["CommandInProgress"] = 228,
        ["GetDocuments"] = 273, ["ActivateDoc3"] = 306, ["SetAddinCallbackInfo2"] = 310, ["StartupProcessCompleted"] = 311,
    };
    public static readonly IReadOnlyDictionary<string, int> ModelDoc2Members = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["GetTitle"] = 65607, ["GetPathName"] = 65608, ["GetType"] = 65609, ["IsOpenedReadOnly"] = 65914, ["VersionHistory"] = 65922,
        ["SetReadOnlyState"] = 65938, ["GetSaveFlag"] = 65986, ["ReloadOrReplace"] = 66289, ["Extension"] = 66306,
    };
    public static readonly IReadOnlyDictionary<string, int> ModelDocExtensionMembers = new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["GetAdvancedSaveAsOptions"] = 314, ["SaveAs3"] = 315,
    };
    // IFrame.SetStatusBarText; IAdvancedSaveAsOptions.SaveAsPreviousVersion (not used: it can't
    // write over the document's own path).
    public const int FrameSetStatusBarText = 10;
    public const int AdvancedSaveAsPreviousVersion = 10;

    // ---- Enums ------------------------------------------------------------------------------------

    // swFileSaveTypes_e.
    public const int SaveTypeSave = 1, SaveTypeSaveAs = 2, SaveTypeSaveAsCopy = 3, SaveTypeSaveAsCopyAndOpen = 4;
    // swDestroyNotifyType_e.
    public const int DestroyTypeDestroy = 0, DestroyTypeHidden = 1;
    // swOpenDocOptions_e, swSaveAsOptions_e.
    public const int OpenSilent = 1, OpenReadOnly = 2, SaveAsSilent = 1, SaveAsCopy = 2;
    // swSaveToVersion_e: do not upgrade, one release back, two releases back.
    public const int SaveToVersionDoNotUpgrade = 0, SaveToVersionPenultimate = 1, SaveToVersionAntepenultimate = 2;
    // swCompatibilityDialogOptions_e and swVersionCompatibilityResult_e (2026 SP3).
    public const int CompatibilityAlwaysShow = 0, CompatibilityShowOnIncompatible = 1, CompatibilityNeverShow = 2;
    public const int CompatibilityCompleted = 0, CompatibilityFailUnknown = 1, CompatibilityFailFileNotResolved = 2, CompatibilityFailInvalidVersion = 3;
    // swComponentReloadError_e.
    public const int ReloadOkay = 0, ReloadModifiedNotReloaded = 3, ReloadReadOnlyChanged = 16;
    // swDisplayStateOpts_e: this display state.
    public const int ThisDisplayState = 1;
    // The Running Object Table entry SolidWorks registers for each session ("!" is the item
    // moniker's delimiter: the probe records which form a real one shows).
    public static string RotName(int pid) => "SolidWorks_PID_" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture);
    public const string ProgId = "SldWorks.Application";
    public const string ProcessName = "SLDWORKS";
    // The add-in object of SolidWorks Simulation (only when it is loaded).
    public const string SimulationAddIn = "SldWorks.Simulation";
}

// swDocumentTypes_e.
internal static class SwDocTypes
{
    public const int None = 0, Part = 1, Assembly = 2, Drawing = 3;
}

internal sealed record DocumentEvents(int DocType, Guid Iid, int FileSaveNotify, int FileSaveAsNotify2, int FileSavePostNotify, int ModifyNotify, int DestroyNotify2);
