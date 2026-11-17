namespace Trainer.Infrastructure.Persistence;

/// <summary>
/// Marks a migration as a deliberate contract step (it drops, renames or tightens structure). It only lifts the static
/// expand-only check in <c>MigrationSafetyTests</c>; the deploy workflow still runs the deployed release's own tests
/// against the migrated database, so a contract step can ship only after the deployed release stopped using what it removes.
/// </summary>
/// <param name="reason">Why the step is safe, for the reviewer who reads the migration.</param>
[AttributeUsage(AttributeTargets.Class)]
public sealed class ContractMigrationAttribute(string reason) : Attribute
{
    public string Reason { get; } = reason;
}
