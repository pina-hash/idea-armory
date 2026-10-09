using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Armory.FakeSolidWorks;

// COM by hand, through ComWrappers: every vtable here is written out (no type library, no
// built-in IDispatch), so the link meets IDispatch the way a real server answers it:
// GetIDsOfNames by name, Invoke by DISPID, VARIANTs in and out, events fired through the sink's
// own IDispatch::Invoke with the real DISPIDs.
internal static class Hr
{
    public const int S_OK = 0, E_NOTIMPL = unchecked((int)0x80004001), E_NOINTERFACE = unchecked((int)0x80004002), E_POINTER = unchecked((int)0x80004003),
        E_FAIL = unchecked((int)0x80004005), DISP_E_UNKNOWNNAME = unchecked((int)0x80020006), DISP_E_MEMBERNOTFOUND = unchecked((int)0x80020003),
        CONNECT_E_NOCONNECTION = unchecked((int)0x80040200), CONNECT_E_CANNOTCONNECT = unchecked((int)0x80040202);
}

internal static class Iids
{
    public static readonly Guid IDispatch = new("00020400-0000-0000-C000-000000000046");
    public static readonly Guid IConnectionPointContainer = new("B196B284-BAB4-101A-B69C-00AA00341D07");
    public static readonly Guid IConnectionPoint = new("B196B286-BAB4-101A-B69C-00AA00341D07");
    public static readonly Guid IMessageFilter = new("00000016-0000-0000-C000-000000000046");
    public static readonly Guid IExternalConnection = new("00000019-0000-0000-C000-000000000046");
}

[StructLayout(LayoutKind.Explicit, Size = 24)]
internal struct Variant
{
    [FieldOffset(0)] public ushort Vt;
    [FieldOffset(8)] public long I8;
    [FieldOffset(8)] public int I4;
    [FieldOffset(8)] public short Bool;
    [FieldOffset(8)] public IntPtr Pointer;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct DispParams
{
    public Variant* Args;
    public int* NamedArgs;
    public uint Count;
    public uint NamedCount;
}

internal static class Vt
{
    public const ushort Empty = 0, Null = 1, I2 = 2, I4 = 3, Bstr = 8, Dispatch = 9, Bool = 11, Variant = 12, Unknown = 13, I8 = 20, UI4 = 19, Int = 22, UInt = 23,
        Array = 0x2000, ByRef = 0x4000;
}

// An object the fake serves: IDispatch by name, and optionally connection points.
internal abstract class Served
{
    internal abstract IReadOnlyDictionary<string, int> Members { get; }
    // A member call: args in the caller's order; byref ones may be replaced (an out argument).
    internal abstract object? Invoke(int dispid, object?[] args);
    internal virtual IReadOnlyList<Guid> EventInterfaces => [];
    internal Dictionary<Guid, ConnectionPoint> Points { get; } = [];
    // Strong references other processes hold now (the Running Object Table's among them), as
    // COM counts them through IExternalConnection.
    internal int Connections;
    internal ConnectionPoint? PointFor(Guid iid)
    {
        if (!EventInterfaces.Contains(iid)) return null;
        if (!Points.TryGetValue(iid, out var point)) Points[iid] = point = new ConnectionPoint(this, iid);
        return point;
    }
}

// One connection point: the sinks advised for one event interface.
internal sealed class ConnectionPoint(Served owner, Guid iid)
{
    internal Served Owner { get; } = owner;
    internal Guid Iid { get; } = iid;
    internal Dictionary<int, IntPtr> Sinks { get; } = [];
    private int next;
    internal int NextCookie() => ++next;
}

// The fake's own message filter: rejects incoming calls (SERVERCALL_RETRYLATER) until a time.
internal sealed class Filter
{
    internal long RejectUntil;
    internal int Rejected;
}

internal sealed unsafe class FakeComWrappers : ComWrappers
{
    internal static readonly FakeComWrappers Instance = new();
    private static readonly ComInterfaceEntry* DispatchOnly, DispatchAndContainer, PointEntries, FilterEntries;
    internal static Action<string> Write = Console.WriteLine;

    static FakeComWrappers()
    {
        GetIUnknownImpl(out var qi, out var addRef, out var release);
        var dispatch = Table(qi, addRef, release,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)&GetTypeInfoCount,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)&GetTypeInfo,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint, uint, int*, int>)&GetIDsOfNames,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, uint, ushort, DispParams*, Variant*, IntPtr, uint*, int>)&Invoke);
        var container = Table(qi, addRef, release,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&EnumConnectionPoints,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)&FindConnectionPoint);
        var point = Table(qi, addRef, release,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, Guid*, int>)&GetConnectionInterface,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&GetConnectionPointContainer,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int*, int>)&Advise,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, int>)&Unadvise,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)&EnumConnections);
        var external = Table(qi, addRef, release,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint>)&AddConnection,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, uint, uint, int, uint>)&ReleaseConnection);
        var filter = Table(qi, addRef, release,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, int, IntPtr, int, IntPtr, int>)&HandleInComingCall,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, int>)&RetryRejectedCall,
            (IntPtr)(delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int, int, int>)&MessagePending);
        DispatchOnly = Entries((Iids.IDispatch, dispatch));
        DispatchAndContainer = Entries((Iids.IDispatch, dispatch), (Iids.IConnectionPointContainer, container), (Iids.IExternalConnection, external));
        PointEntries = Entries((Iids.IConnectionPoint, point));
        FilterEntries = Entries((Iids.IMessageFilter, filter));
    }

    private static IntPtr Table(IntPtr qi, IntPtr addRef, IntPtr release, params IntPtr[] methods)
    {
        var table = (IntPtr*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(FakeComWrappers), IntPtr.Size * (3 + methods.Length));
        table[0] = qi;
        table[1] = addRef;
        table[2] = release;
        for (var i = 0; i < methods.Length; i++) table[3 + i] = methods[i];
        return (IntPtr)table;
    }

    private static ComInterfaceEntry* Entries(params (Guid Iid, IntPtr Table)[] items)
    {
        var entries = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(FakeComWrappers), sizeof(ComInterfaceEntry) * items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            entries[i].IID = items[i].Iid;
            entries[i].Vtable = items[i].Table;
        }
        return entries;
    }

    protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
    {
        switch (obj)
        {
            case ConnectionPoint: count = 1; return PointEntries;
            case Filter: count = 1; return FilterEntries;
            case Served served when served.EventInterfaces.Count > 0: count = 3; return DispatchAndContainer;
            case Served: count = 1; return DispatchOnly;
            default: count = 0; return null;
        }
    }

    protected override object CreateObject(IntPtr externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();
    protected override void ReleaseObjects(IEnumerable objects) => throw new NotSupportedException();

    // An interface pointer for a served object (AddRef'd: the receiver releases it).
    internal static IntPtr Pointer(object obj, Guid iid)
    {
        var unknown = Instance.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);
        try { return Marshal.QueryInterface(unknown, iid, out var pointer) == 0 ? pointer : IntPtr.Zero; }
        finally { Marshal.Release(unknown); }
    }

    // The references others hold on a served object's IUnknown right now.
    internal static int References(object obj)
    {
        var unknown = Instance.GetOrCreateComInterfaceForObject(obj, CreateComInterfaceFlags.None);
        Marshal.AddRef(unknown);
        Marshal.Release(unknown);
        return Marshal.Release(unknown);
    }

    // ---- IDispatch ------------------------------------------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfoCount(IntPtr self, uint* count)
    {
        if (count == null) return Hr.E_POINTER;
        *count = 0;
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetTypeInfo(IntPtr self, uint index, uint lcid, IntPtr* info)
    {
        if (info != null) *info = IntPtr.Zero;
        return Hr.E_NOTIMPL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetIDsOfNames(IntPtr self, Guid* iid, IntPtr* names, uint count, uint lcid, int* ids)
    {
        try
        {
            var served = ComInterfaceDispatch.GetInstance<Served>((ComInterfaceDispatch*)self);
            var all = true;
            for (var i = 0; i < count; i++)
            {
                var name = Marshal.PtrToStringUni(names[i]) ?? "";
                var match = served.Members.FirstOrDefault(m => string.Equals(m.Key, name, StringComparison.OrdinalIgnoreCase));
                if (i == 0 && match.Key is not null) ids[i] = match.Value;
                else { ids[i] = -1; all = false; }
            }
            return all ? Hr.S_OK : Hr.DISP_E_UNKNOWNNAME;
        }
        catch (Exception error) { Write("error GetIDsOfNames " + error.Message); return Hr.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Invoke(IntPtr self, int dispid, Guid* iid, uint lcid, ushort flags, DispParams* parameters, Variant* result, IntPtr excepInfo, uint* argErr)
    {
        try
        {
            var served = ComInterfaceDispatch.GetInstance<Served>((ComInterfaceDispatch*)self);
            var count = parameters == null ? 0 : (int)parameters->Count;
            var args = new object?[count];
            for (var i = 0; i < count; i++) args[count - 1 - i] = Read(&parameters->Args[i]);
            var before = (object?[])args.Clone();
            var value = served.Invoke(dispid, args);
            // Out arguments (by reference) carry their new values back.
            for (var i = 0; i < count; i++)
            {
                var variant = &parameters->Args[count - 1 - i];
                if ((variant->Vt & Vt.ByRef) != 0 && !ReferenceEquals(before[i], args[i]) && !Equals(before[i], args[i])) WriteByRef(variant, args[i]);
            }
            if (result != null) WriteValue(result, value);
            return Hr.S_OK;
        }
        catch (MissingMemberException) { return Hr.DISP_E_MEMBERNOTFOUND; }
        catch (Exception error) { Write("error Invoke " + dispid + " " + error.GetType().Name + " " + error.Message); return Hr.E_FAIL; }
    }

    // ---- IConnectionPointContainer, IConnectionPoint -----------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumConnectionPoints(IntPtr self, IntPtr* points)
    {
        if (points != null) *points = IntPtr.Zero;
        return Hr.E_NOTIMPL;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int FindConnectionPoint(IntPtr self, Guid* iid, IntPtr* point)
    {
        try
        {
            *point = IntPtr.Zero;
            var served = ComInterfaceDispatch.GetInstance<Served>((ComInterfaceDispatch*)self);
            if (served.PointFor(*iid) is not { } found) return Hr.CONNECT_E_NOCONNECTION;
            *point = Pointer(found, Iids.IConnectionPoint);
            return Hr.S_OK;
        }
        catch (Exception error) { Write("error FindConnectionPoint " + error.Message); return Hr.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetConnectionInterface(IntPtr self, Guid* iid)
    {
        *iid = ComInterfaceDispatch.GetInstance<ConnectionPoint>((ComInterfaceDispatch*)self).Iid;
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GetConnectionPointContainer(IntPtr self, IntPtr* container)
    {
        *container = Pointer(ComInterfaceDispatch.GetInstance<ConnectionPoint>((ComInterfaceDispatch*)self).Owner, Iids.IConnectionPointContainer);
        return Hr.S_OK;
    }

    // As a real server does: the sink is asked for the event interface itself. Across processes
    // that question needs the event interface's proxy registered, which SolidWorks' own install
    // does (its type library); on a computer without SolidWorks nothing registers it, so the
    // fake then asks for IDispatch, which every sink answers and which the events arrive on anyway.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Advise(IntPtr self, IntPtr sink, int* cookie)
    {
        try
        {
            var point = ComInterfaceDispatch.GetInstance<ConnectionPoint>((ComInterfaceDispatch*)self);
            if (sink == IntPtr.Zero) return Hr.E_POINTER;
            if (Marshal.QueryInterface(sink, point.Iid, out var events) != 0
                && Marshal.QueryInterface(sink, Iids.IDispatch, out events) != 0)
            {
                Write("advise refused " + point.Iid);
                return Hr.CONNECT_E_CANNOTCONNECT;
            }
            var id = point.NextCookie();
            point.Sinks[id] = events;
            *cookie = id;
            Write($"advise {point.Iid} {id}");
            return Hr.S_OK;
        }
        catch (Exception error) { Write("error Advise " + error.Message); return Hr.E_FAIL; }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Unadvise(IntPtr self, int cookie)
    {
        var point = ComInterfaceDispatch.GetInstance<ConnectionPoint>((ComInterfaceDispatch*)self);
        if (!point.Sinks.Remove(cookie, out var sink)) return Hr.CONNECT_E_NOCONNECTION;
        Marshal.Release(sink);
        Write($"unadvise {point.Iid} {cookie}");
        return Hr.S_OK;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int EnumConnections(IntPtr self, IntPtr* connections)
    {
        if (connections != null) *connections = IntPtr.Zero;
        return Hr.E_NOTIMPL;
    }

    // ---- IMessageFilter (the fake's own, to answer "busy") ------------------------------------------

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int HandleInComingCall(IntPtr self, int callType, IntPtr task, int tickCount, IntPtr info)
    {
        var filter = ComInterfaceDispatch.GetInstance<Filter>((ComInterfaceDispatch*)self);
        if (Environment.TickCount64 >= Interlocked.Read(ref filter.RejectUntil)) return 0; // SERVERCALL_ISHANDLED
        Interlocked.Increment(ref filter.Rejected);
        return 2; // SERVERCALL_RETRYLATER
    }

    // ---- IExternalConnection: COM says when other processes take or let go a strong reference ----

    private const uint ExtconnStrong = 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddConnection(IntPtr self, uint kind, uint reserved)
    {
        var served = ComInterfaceDispatch.GetInstance<Served>((ComInterfaceDispatch*)self);
        return (uint)((kind & ExtconnStrong) != 0 ? Interlocked.Increment(ref served.Connections) : served.Connections);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint ReleaseConnection(IntPtr self, uint kind, uint reserved, int lastReleaseCloses)
    {
        var served = ComInterfaceDispatch.GetInstance<Served>((ComInterfaceDispatch*)self);
        return (uint)((kind & ExtconnStrong) != 0 ? Interlocked.Decrement(ref served.Connections) : served.Connections);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int RetryRejectedCall(IntPtr self, IntPtr task, int tickCount, int rejectType) => -1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int MessagePending(IntPtr self, IntPtr task, int tickCount, int pendingType) => 2; // PENDINGMSG_WAITDEFPROCESS

    // ---- VARIANTs -------------------------------------------------------------------------------------

    internal static object? Read(Variant* v)
    {
        var vt = v->Vt;
        if ((vt & Vt.ByRef) != 0)
        {
            var target = v->Pointer;
            if (target == IntPtr.Zero) return null;
            return (vt & ~Vt.ByRef) switch
            {
                Vt.Variant => Read((Variant*)target),
                Vt.I4 or Vt.Int => *(int*)target,
                Vt.Bool => *(short*)target != 0,
                Vt.Bstr => Marshal.PtrToStringBSTR(*(IntPtr*)target),
                Vt.Dispatch or Vt.Unknown => Unwrap(*(IntPtr*)target),
                _ => null,
            };
        }
        return vt switch
        {
            Vt.Empty or Vt.Null => null,
            Vt.I2 => (int)*(short*)&v->I8,
            Vt.I4 or Vt.Int or Vt.UI4 or Vt.UInt => v->I4,
            Vt.I8 => v->I8,
            Vt.Bool => v->Bool != 0,
            Vt.Bstr => v->Pointer == IntPtr.Zero ? "" : Marshal.PtrToStringBSTR(v->Pointer),
            Vt.Dispatch or Vt.Unknown => Unwrap(v->Pointer),
            _ => Marshal.GetObjectForNativeVariant((IntPtr)v),
        };
    }

    // One of the fake's own objects passed back in (CloseAndReopen's document), or a foreign pointer.
    private static object? Unwrap(IntPtr pointer)
        => pointer == IntPtr.Zero ? null : ComWrappers.TryGetObject(pointer, out var found) ? found : pointer;

    internal static void WriteValue(Variant* v, object? value)
    {
        *v = default;
        switch (value)
        {
            case null: v->Vt = Vt.Empty; break;
            case bool b: v->Vt = Vt.Bool; v->Bool = (short)(b ? -1 : 0); break;
            case int i: v->Vt = Vt.I4; v->I4 = i; break;
            case string s: v->Vt = Vt.Bstr; v->Pointer = Marshal.StringToBSTR(s); break;
            case Served served: v->Vt = Vt.Dispatch; v->Pointer = Pointer(served, Iids.IDispatch); break;
            case string[] strings: v->Vt = Vt.Array | Vt.Bstr; v->Pointer = StringArray(strings); break;
            case Served[] items: v->Vt = Vt.Array | Vt.Dispatch; v->Pointer = DispatchArray(items); break;
            default: Marshal.GetNativeVariantForObject(value, (IntPtr)v); break;
        }
    }

    private static void WriteByRef(Variant* v, object? value)
    {
        var target = v->Pointer;
        if (target == IntPtr.Zero) return;
        switch (v->Vt & ~Vt.ByRef)
        {
            case Vt.Variant:
                VariantClear((Variant*)target);
                WriteValue((Variant*)target, value);
                break;
            case Vt.I4 or Vt.Int:
                *(int*)target = value is int i ? i : 0;
                break;
            case Vt.Bool:
                *(short*)target = (short)(value is true ? -1 : 0);
                break;
            case Vt.Dispatch:
                *(IntPtr*)target = value is Served served ? Pointer(served, Iids.IDispatch) : IntPtr.Zero;
                break;
        }
    }

    private static IntPtr StringArray(string[] strings)
    {
        var array = SafeArrayCreateVector(Vt.Bstr, 0, (uint)strings.Length);
        for (var i = 0; i < strings.Length; i++)
        {
            var bstr = Marshal.StringToBSTR(strings[i]);
            var index = i;
            SafeArrayPutElement(array, &index, (void*)bstr);
            Marshal.FreeBSTR(bstr);
        }
        return array;
    }

    private static IntPtr DispatchArray(Served[] items)
    {
        var array = SafeArrayCreateVector(Vt.Dispatch, 0, (uint)items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var pointer = Pointer(items[i], Iids.IDispatch);
            var index = i;
            SafeArrayPutElement(array, &index, (void*)pointer);
            Marshal.Release(pointer);
        }
        return array;
    }

    // Fires one event at one sink, through the sink's IDispatch::Invoke; its int answer back.
    internal static int Fire(IntPtr sink, int dispid, params object?[] args)
    {
        var count = args.Length;
        var argv = stackalloc Variant[Math.Max(count, 1)];
        for (var i = 0; i < count; i++) WriteValue(&argv[count - 1 - i], args[i]);
        var parameters = new DispParams { Args = argv, NamedArgs = null, Count = (uint)count, NamedCount = 0 };
        Variant result = default;
        var none = Guid.Empty;
        uint argError = 0;
        var table = *(IntPtr**)sink;
        var invoke = (delegate* unmanaged[Stdcall]<IntPtr, int, Guid*, uint, ushort, DispParams*, Variant*, IntPtr, uint*, int>)table[6];
        var hr = invoke(sink, dispid, &none, 0, 1 /* DISPATCH_METHOD */, &parameters, &result, IntPtr.Zero, &argError);
        for (var i = 0; i < count; i++) VariantClear(&argv[i]);
        var answer = hr < 0 ? hr : Read(&result) is int n ? n : 0;
        VariantClear(&result);
        return answer;
    }

    [DllImport("oleaut32.dll")]
    private static extern IntPtr SafeArrayCreateVector(ushort vt, int lowerBound, uint count);

    [DllImport("oleaut32.dll")]
    private static extern int SafeArrayPutElement(IntPtr array, int* index, void* value);

    [DllImport("oleaut32.dll")]
    internal static extern int VariantClear(Variant* v);
}
