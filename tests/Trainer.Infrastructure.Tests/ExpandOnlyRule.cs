using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace Trainer.Infrastructure.Tests;

/// <summary>
/// The static half of the rolling-deploy guarantee: a migration that keeps the previous release working may only add
/// structure the previous image can ignore. Anything else needs <c>[ContractMigration]</c>.
/// </summary>
internal static partial class ExpandOnlyRule
{
    public static IReadOnlyList<string> Violations(
        string migrationId, IReadOnlyList<MigrationOperation> operations, IReadOnlySet<string> existingTables)
    {
        List<string> found = [];
        foreach (MigrationOperation operation in operations)
        {
            string? reason = Check(operation, existingTables);
            if (reason is not null)
            {
                found.Add($"{migrationId}: {operation.GetType().Name} {reason}");
            }
        }

        return found;
    }

    private static string? Check(MigrationOperation operation, IReadOnlySet<string> existingTables)
    {
        switch (operation)
        {
            case DropTableOperation drop:
                return $"drops table '{drop.Name}'";
            case DropColumnOperation drop:
                return $"drops column '{drop.Table}.{drop.Name}'";
            case RenameTableOperation rename:
                return $"renames table '{rename.Name}'";
            case RenameColumnOperation rename:
                return $"renames column '{rename.Table}.{rename.Name}'";
            case DropIndexOperation drop when TableExisted(drop.Table, existingTables):
                return $"drops index '{drop.Name}' on existing table '{drop.Table}'";
            case AlterColumnOperation alter:
                return CheckAlter(alter);
            case AddColumnOperation add when !add.IsNullable && add.DefaultValue is null && add.DefaultValueSql is null:
                return $"adds non-nullable column '{add.Table}.{add.Name}' without a default";
            case CreateIndexOperation { IsUnique: true } index when TableExisted(index.Table, existingTables):
                return $"creates unique index '{index.Name}' on existing table '{index.Table}'";
            case AddPrimaryKeyOperation pk when TableExisted(pk.Table, existingTables):
                return $"adds primary key '{pk.Name}' on existing table '{pk.Table}'";
            case AddForeignKeyOperation fk when TableExisted(fk.Table, existingTables):
                return $"adds foreign key '{fk.Name}' on existing table '{fk.Table}'";
            case AddCheckConstraintOperation check when TableExisted(check.Table, existingTables):
                return $"adds check constraint '{check.Name}' on existing table '{check.Table}'";
            case AddUniqueConstraintOperation unique when TableExisted(unique.Table, existingTables):
                return $"adds unique constraint '{unique.Name}' on existing table '{unique.Table}'";
            case SqlOperation:
                return "runs raw SQL";
            default:
                return null;
        }
    }

    private static bool TableExisted(string? table, IReadOnlySet<string> existingTables) => table is not null && existingTables.Contains(table);

    private static string? CheckAlter(AlterColumnOperation alter)
    {
        string column = $"'{alter.Table}.{alter.Name}'";
        if (!alter.IsNullable && alter.OldColumn.IsNullable)
        {
            return $"makes column {column} non-nullable";
        }

        if ((alter.OldColumn.DefaultValue is not null || alter.OldColumn.DefaultValueSql is not null)
            && alter.DefaultValue is null && alter.DefaultValueSql is null)
        {
            return $"removes the default of column {column}";
        }

        if (Narrows(alter))
        {
            return $"narrows the type or length of column {column}";
        }

        return null;
    }

    // The previous image may still write values of the old type, so a change is safe only when every old value fits:
    // a longer or equal length, a varchar widened to text, or a wider integer. Anything unrecognised counts as narrowing.
    private static bool Narrows(AlterColumnOperation alter)
    {
        if (alter.MaxLength is { } newLength
            && (alter.OldColumn.MaxLength is not { } oldLength || newLength < oldLength))
        {
            return true;
        }

        string? newType = alter.ColumnType;
        string? oldType = alter.OldColumn.ColumnType;
        if (newType is not null && oldType is not null)
        {
            return !string.Equals(newType, oldType, StringComparison.Ordinal) && !ColumnTypeWidens(oldType, newType);
        }

        Type newClr = Nullable.GetUnderlyingType(alter.ClrType) ?? alter.ClrType;
        Type oldClr = Nullable.GetUnderlyingType(alter.OldColumn.ClrType) ?? alter.OldColumn.ClrType;
        return newClr != oldClr && !ClrTypeWidens(oldClr, newClr);
    }

    private static bool ColumnTypeWidens(string oldType, string newType)
    {
        Match newMatch = SizedType().Match(newType);
        Match oldMatch = SizedType().Match(oldType);
        if (oldMatch.Success)
        {
            string oldBase = oldMatch.Groups["base"].Value;
            if (newMatch.Success)
            {
                return string.Equals(newMatch.Groups["base"].Value, oldBase, StringComparison.Ordinal)
                    && int.Parse(newMatch.Groups["size"].Value, CultureInfo.InvariantCulture)
                        >= int.Parse(oldMatch.Groups["size"].Value, CultureInfo.InvariantCulture);
            }

            return string.Equals(newType, "text", StringComparison.Ordinal)
                && (oldBase.StartsWith("character", StringComparison.Ordinal) || oldBase.StartsWith("varchar", StringComparison.Ordinal));
        }

        return IntegerRank.TryGetValue(oldType, out int oldRank)
            && IntegerRank.TryGetValue(newType, out int newRank)
            && newRank >= oldRank;
    }

    private static bool ClrTypeWidens(Type oldType, Type newType) =>
        ClrIntegerRank.TryGetValue(oldType, out int oldRank)
        && ClrIntegerRank.TryGetValue(newType, out int newRank)
        && newRank >= oldRank;

    private static readonly Dictionary<string, int> IntegerRank = new(StringComparer.Ordinal)
    {
        ["smallint"] = 1,
        ["integer"] = 2,
        ["bigint"] = 3,
    };

    private static readonly Dictionary<Type, int> ClrIntegerRank = new()
    {
        [typeof(short)] = 1,
        [typeof(int)] = 2,
        [typeof(long)] = 3,
    };

    [GeneratedRegex(@"^(?<base>[a-z ]+)\((?<size>\d+)\)$")]
    private static partial Regex SizedType();
}
