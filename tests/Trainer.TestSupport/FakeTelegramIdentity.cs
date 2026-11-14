namespace Trainer.TestSupport;

/// <summary>
/// The Telegram account a <see cref="FakeTelegramOidcServer"/> signs in. <paramref name="Id"/> is the Bot-API user id
/// that Telegram delivers in the <c>id</c> claim; the OIDC <c>sub</c> is a separate opaque value. A null name or
/// username omits the corresponding claim, as Telegram does for accounts without one.
/// </summary>
public sealed record FakeTelegramIdentity(long Id, string? Name, string? Username);
