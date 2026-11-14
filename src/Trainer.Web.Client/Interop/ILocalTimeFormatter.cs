namespace Trainer.Web.Client.Interop;

/// <summary>
/// Formats an instant for display in the device's time zone and the page language. The client has no culture data
/// (invariant globalization, ADR 0013), so the browser's <c>Intl</c> does it.
/// </summary>
internal interface ILocalTimeFormatter
{
    /// <param name="language">The page language, <c>ru</c> or <c>en</c>.</param>
    /// <param name="withTime">Adds hours and minutes to the date.</param>
    string Format(string language, DateTimeOffset value, bool withTime);
}
