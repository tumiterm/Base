namespace Base.Web.Formatting;

/// <summary>A user's account state in words and badge tone, the same on every screen.</summary>
public static class UserDisplay
{
    public static (string Label, string Tone) Status(bool isLockedOut, bool hasPassword, DateTimeOffset? lastSignInAt) =>
        isLockedOut ? ("Locked", "danger")
        : !hasPassword ? ("Invited", "warning")
        : lastSignInAt is null ? ("Not signed in yet", "secondary")
        : ("Active", "success");
}
