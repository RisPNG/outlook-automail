namespace OutlookAutomail.UI;

internal static class AppIcon
{
    public static Icon Default { get; } = new(typeof(AppIcon).Assembly.GetManifestResourceStream("OutlookAutomail.AppIcon.ico")!);
}
