using Microsoft.AspNetCore.Components.Forms;

namespace Trainer.Web.Security;

/// <summary>
/// The static edition's way to persist no antiforgery token. The framework registers the antiforgery state provider as a
/// persistent service, so every page that persists component state (every island page) asks it for a request token,
/// which sets the antiforgery cookie. The static edition has no write endpoint to protect and must set no cookie.
/// </summary>
/// <remarks>
/// The provider cannot be replaced by a stub: the endpoint renderer casts the registered provider to its own internal
/// type on every request. Only the persistence registration is removed, found by the name of its internal service type and its public generic argument. A
/// framework change that moves it makes <see cref="Apply"/> throw at startup instead of leaving cookies on pages.
/// </remarks>
internal static class NoPersistedAntiforgery
{
    private const string RegistrationType = "Microsoft.AspNetCore.Components.IPersistentServiceRegistration";

    public static void Apply(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        ServiceDescriptor[] registrations =
        [
            .. services.Where(descriptor =>
                string.Equals(descriptor.ServiceType.FullName, RegistrationType, StringComparison.Ordinal)
                && descriptor.ImplementationInstance is { } registration
                && registration.GetType().GenericTypeArguments.Contains(typeof(AntiforgeryStateProvider))),
        ];
        foreach (ServiceDescriptor registration in registrations)
        {
            services.Remove(registration);
        }

        if (registrations.Length == 0)
        {
            throw new InvalidOperationException(
                "The antiforgery state provider has no persistent service registration to remove; "
                + "NoPersistedAntiforgery must be updated for this framework version.");
        }
    }
}
