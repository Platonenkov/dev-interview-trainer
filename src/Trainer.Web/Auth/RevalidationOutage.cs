namespace Trainer.Web.Auth;

/// <summary>
/// Marks a request whose session cookie could not be revalidated because the database failed. Such a request runs as
/// anonymous (fail closed), but it is not a sign-out: <c>/api</c> answers it 503 <c>server-error</c> instead of 401, so
/// island clients retry with back-off instead of stopping sync and showing "signed out", and the sign-in page does not
/// publish the anonymous owner to the user's other tabs.
/// </summary>
public static class RevalidationOutage
{
    private static readonly object Key = new();

    public static void Mark(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        context.Items[Key] = true;
    }

    public static bool IsMarked(HttpContext? context) => context?.Items.ContainsKey(Key) == true;
}
