using System.Runtime.CompilerServices;
using Notifications = global::Windows.UI.Notifications;
using XmlDom = global::Windows.Data.Xml.Dom;

namespace Armory.Agent;

// Windows' notifications through the Windows SDK projection (Windows.UI.Notifications), the
// only class in Armory that touches WinRT. It shows notifications as the AppUserModelID that
// ShellIdentity registers (IdeaBosco.Armory, which names the app and its icon in them). Built
// only on Windows by the installed copy; tests use their own IToastPlatform.
internal sealed class WindowsToasts(string appId) : IToastPlatform
{
    private Notifications.ToastNotifier? notifier;

    public ToastSetting Setting
    {
        [MethodImpl(MethodImplOptions.NoInlining)]
        get => Notifier().Setting == Notifications.NotificationSetting.Enabled ? ToastSetting.Enabled : ToastSetting.Off;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Show(ToastContent toast)
    {
        var xml = new XmlDom.XmlDocument();
        xml.LoadXml(ToastXml.Build(toast));
        Notifier().Show(new Notifications.ToastNotification(xml)
        {
            Tag = toast.Tag,
            Group = toast.Group,
            ExpirationTime = DateTimeOffset.Now + ToastXml.Expires,
        });
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Remove(string tag, string group) => Notifications.ToastNotificationManager.History.Remove(tag, group, appId);

    [MethodImpl(MethodImplOptions.NoInlining)]
    public void Clear() => Notifications.ToastNotificationManager.History.Clear(appId);

    private Notifications.ToastNotifier Notifier() => notifier ??= Notifications.ToastNotificationManager.CreateToastNotifier(appId);
}
