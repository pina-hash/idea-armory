using System.Dynamic;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Armory.Probe;

// Invoke IDispatch by name without the C# dynamic binder requiring a working registered
// type library. Some installations expose IDispatch but fail GetTypeInfo.
internal sealed class ComDispatch(object target) : DynamicObject
{
    private static object? Wrap(object? value) => value is not null && Marshal.IsComObject(value) ? new ComDispatch(value) : value;
    public override bool TryGetMember(GetMemberBinder binder, out object? result)
    {
        result = Wrap(target.GetType().InvokeMember(binder.Name, BindingFlags.GetProperty, null, target, null, CultureInfo.InvariantCulture));
        return true;
    }
    public override bool TryInvokeMember(InvokeMemberBinder binder, object?[]? args, out object? result)
    {
        args ??= [];
        if (binder.Name == "SaveAs" && args.Length == 6 && args[3] is null) args[3] = new DispatchWrapper(null);
        ParameterModifier[]? modifiers = null;
        if (binder.Name is "OpenDoc6" or "SaveAs" or "ActivateDoc3")
        {
            var modifier = new ParameterModifier(args.Length);
            modifier[args.Length - 1] = true;
            if (binder.Name != "ActivateDoc3") modifier[args.Length - 2] = true;
            modifiers = [modifier];
        }
        result = Wrap(target.GetType().InvokeMember(binder.Name, BindingFlags.InvokeMethod, null, target, args, modifiers, CultureInfo.InvariantCulture, null));
        return true;
    }
}
