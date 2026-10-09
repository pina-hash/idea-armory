using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Armory.SolidWorks;

// Late-bound calls into SolidWorks by member name (IDispatch.GetIDsOfNames, then Invoke), the
// pattern tools/Armory.Probe measured on a real SolidWorks: Type.InvokeMember, never C#
// dynamic, because some installations expose IDispatch but fail GetTypeInfo. Every reference
// received is released exactly once by whoever received it (Release), so the RCW's own count
// stays balanced and a document kept for its events is never released from under it.
internal static class Com
{
    public static object? Get(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture);

    // A property with arguments (IModelDocExtension.CustomPropertyManager[configuration]).
    public static object? Get(object target, string name, params object?[] args)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, args, CultureInfo.InvariantCulture);

    public static object? Call(object target, string name, params object?[] args)
        => target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, CultureInfo.InvariantCulture);

    // A call with by-reference arguments (out Errors, out Warnings...): args holds their values
    // afterwards. byRef names the positions passed by reference.
    public static object? CallRef(object target, string name, object?[] args, params int[] byRef)
    {
        var modifier = new ParameterModifier(args.Length);
        foreach (var index in byRef) modifier[index] = true;
        return target.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, [modifier], CultureInfo.InvariantCulture, null);
    }

    public static string? String(object target, string name) => Get(target, name) as string;
    public static string? CallString(object target, string name, params object?[] args) => Call(target, name, args) as string;
    public static bool CallBool(object target, string name, params object?[] args) => Call(target, name, args) is true;
    public static int? CallInt(object target, string name, params object?[] args) => Call(target, name, args) is int value ? value : null;

    // A VARIANT array (string[], object[]) as strings.
    public static string[] Strings(object? value) => value switch
    {
        string[] strings => strings,
        object[] items => items.Select(i => i as string ?? i?.ToString() ?? "").ToArray(),
        string one => [one],
        _ => [],
    };

    // A VARIANT array of objects (IDispatch[]), or none.
    public static object[] Objects(object? value) => value switch
    {
        object[] items => items.Where(i => i is not null).ToArray(),
        Array array => array.Cast<object?>().Where(i => i is not null).Select(i => i!).ToArray(),
        null => [],
        _ => [value],
    };

    // One reference received, given back.
    public static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); }
            catch (Exception error) when (error is InvalidComObjectException or ArgumentException) { }
        }
    }

    public static void Release(IEnumerable<object> values)
    {
        foreach (var value in values) Release(value);
    }

    // Everything of this object let go at once (a session's end).
    public static void FinalRelease(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.FinalReleaseComObject(value); }
            catch (Exception error) when (error is InvalidComObjectException or ArgumentException) { }
        }
    }

    // The HRESULT behind a COM failure (also inside a TargetInvocationException).
    public static int? HResult(Exception error) => error switch
    {
        TargetInvocationException { InnerException: { } inner } => HResult(inner),
        COMException com => com.HResult,
        InvalidComObjectException => Native.CO_E_OBJNOTCONNECTED,
        _ => null,
    };

    // SolidWorks went away (closed, crashed, or the connection broke).
    public static bool IsGone(Exception error) => HResult(error) is { } hr && Native.IsGone(hr) || error is InvalidComObjectException;

    // Any failure of a call into SolidWorks, which the link survives quietly.
    public static bool IsComFailure(Exception error)
        => error is COMException or InvalidComObjectException or TargetInvocationException or MissingMethodException or InvalidCastException
            or ArgumentException or NotSupportedException;
}
