using Microsoft.Extensions.DependencyInjection;
using Trainer.Application.Users;
using Trainer.Domain.Keys;
using Trainer.Domain.Users;

namespace Trainer.TestSupport;

/// <summary>Counts the database reads of session revalidation; shared by every scope of one test host.</summary>
public sealed class UserAccountCallCounter
{
    private int _getAccess;
    private bool _fail;

    public int GetAccess => Volatile.Read(ref _getAccess);

    /// <summary>When set, <c>GetAccessAsync</c> throws, as a database outage would.</summary>
    public bool FailGetAccess
    {
        get => Volatile.Read(ref _fail);
        set => Volatile.Write(ref _fail, value);
    }

    internal void CountGetAccess() => Interlocked.Increment(ref _getAccess);

    public void Reset() => Interlocked.Exchange(ref _getAccess, 0);
}

/// <summary>
/// Decorates the registered <see cref="IUserAccountService"/> so a test can count <c>GetAccessAsync</c> calls (the
/// revalidation check) without changing any behaviour.
/// </summary>
public static class CountingUserAccountService
{
    /// <summary>Wraps the service registered so far; call it from <c>TrainerWebFactoryOptions.ConfigureServices</c>.</summary>
    public static void Decorate(IServiceCollection services, UserAccountCallCounter counter)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(counter);

        Type inner = services.Last(d => d.ServiceType == typeof(IUserAccountService)).ImplementationType
            ?? throw new InvalidOperationException("IUserAccountService is not registered by implementation type.");
        services.AddScoped<IUserAccountService>(provider =>
            new Counting((IUserAccountService)ActivatorUtilities.CreateInstance(provider, inner), counter));
    }

    private sealed class Counting(IUserAccountService inner, UserAccountCallCounter counter) : IUserAccountService
    {
        public Task<UserAccess> SignInAsync(TelegramProfile profile, CancellationToken ct) => inner.SignInAsync(profile, ct);

        public Task<UserAccess?> GetAccessAsync(long telegramUserId, CancellationToken ct)
        {
            counter.CountGetAccess();
            if (counter.FailGetAccess)
            {
                throw new InvalidOperationException("Simulated database failure.");
            }

            return inner.GetAccessAsync(telegramUserId, ct);
        }

        public Task<BlockResult> SetBlockedAsync(UserId userId, bool blocked, CancellationToken ct) => inner.SetBlockedAsync(userId, blocked, ct);

        public Task SetRolesAsync(UserId userId, IReadOnlyCollection<string> roles, CancellationToken ct) => inner.SetRolesAsync(userId, roles, ct);

        public Task SetPreferredCultureAsync(UserId userId, CultureKey culture, CancellationToken ct) => inner.SetPreferredCultureAsync(userId, culture, ct);
    }
}
