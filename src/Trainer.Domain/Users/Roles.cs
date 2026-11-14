using System.Collections.ObjectModel;

namespace Trainer.Domain.Users;

/// <summary>Role names used in authorization policies and claims.</summary>
public static class Roles
{
    public const string Admin = "Admin";
    public const string Editor = "Editor";

    private static readonly ReadOnlyCollection<string> AllView = Array.AsReadOnly([Admin, Editor]);

    /// <summary>All roles. The list is read-only.</summary>
    public static IReadOnlyList<string> All => AllView;
}
