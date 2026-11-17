using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Trainer.Infrastructure.Persistence;

namespace Trainer.Infrastructure.Tests;

/// <summary>
/// A failed deploy rolls the image back after migrations ran, and every deploy runs the previous image against the
/// migrated schema, so migrations after the Plan 3 baseline may only expand the schema.
/// </summary>
public sealed class MigrationSafetyTests
{
    private const string Baseline = "20261115000000";
    private const string Migration = "20270101000000_Synthetic";

    private static readonly IReadOnlySet<string> ExistingTables = new HashSet<string> { "questions" };

    [Fact]
    public void NewMigrations_AreExpandOnly()
    {
        List<(string Id, Type Type)> all = DiscoverMigrations();
        List<string> violations = [];
        for (int i = 0; i < all.Count; i++)
        {
            (string id, Type type) = all[i];
            if (string.CompareOrdinal(id, Baseline) <= 0 || type.GetCustomAttribute<ContractMigrationAttribute>() is not null)
            {
                continue;
            }

            Migration migration = (Migration)Activator.CreateInstance(type)!;
            IReadOnlySet<string> existing = i == 0 ? new HashSet<string>() : TablesOf(all[i - 1].Type);
            violations.AddRange(ExpandOnlyRule.Violations(id, migration.UpOperations, existing));
        }

        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }

    [Fact]
    public void Discovery_SeesMigrationsOnBothSidesOfTheBaseline()
    {
        List<string> ids = DiscoverMigrations().Select(m => m.Id).ToList();

        Assert.Contains(ids, id => string.CompareOrdinal(id, Baseline) <= 0);
        Assert.Contains(ids, id => string.CompareOrdinal(id, Baseline) > 0);
    }

    [Fact]
    public void ContractMigrationAttribute_IsDetectedOnAClass()
    {
        Assert.NotNull(typeof(MarkedContractStep).GetCustomAttribute<ContractMigrationAttribute>());
        Assert.Null(typeof(UnmarkedStep).GetCustomAttribute<ContractMigrationAttribute>());
    }

    public static IEnumerable<TheoryDataRow<MigrationOperation, string>> Forbidden() =>
    [
        new(new DropTableOperation { Name = "questions" }, "DropTableOperation") { TestDisplayName = "drop table" },
        new(new DropColumnOperation { Table = "questions", Name = "title" }, "DropColumnOperation") { TestDisplayName = "drop column" },
        new(new RenameTableOperation { Name = "questions", NewName = "items" }, "RenameTableOperation") { TestDisplayName = "rename table" },
        new(new RenameColumnOperation { Table = "questions", Name = "title", NewName = "name" }, "RenameColumnOperation") { TestDisplayName = "rename column" },
        new(new DropIndexOperation { Table = "questions", Name = "ix_questions_title" }, "DropIndexOperation") { TestDisplayName = "drop index on existing table" },
        new(new AlterColumnOperation
            {
                Table = "questions", Name = "title", ClrType = typeof(string), IsNullable = false,
                OldColumn = new AddColumnOperation { ClrType = typeof(string), IsNullable = true },
            }, "AlterColumnOperation") { TestDisplayName = "alter column to non-nullable" },
        new(new AlterColumnOperation
            {
                Table = "questions", Name = "title", ClrType = typeof(string), IsNullable = true, MaxLength = 50,
                OldColumn = new AddColumnOperation { ClrType = typeof(string), IsNullable = true, MaxLength = 100 },
            }, "AlterColumnOperation") { TestDisplayName = "alter column narrows the length" },
        new(new AlterColumnOperation
            {
                Table = "questions", Name = "weight", ClrType = typeof(short), IsNullable = true, ColumnType = "smallint",
                OldColumn = new AddColumnOperation { ClrType = typeof(int), IsNullable = true, ColumnType = "integer" },
            }, "AlterColumnOperation") { TestDisplayName = "alter column narrows the type" },
        new(new AlterColumnOperation
            {
                Table = "questions", Name = "title", ClrType = typeof(string), IsNullable = true, ColumnType = "character varying(50)",
                OldColumn = new AddColumnOperation { ClrType = typeof(string), IsNullable = true, ColumnType = "character varying(100)" },
            }, "AlterColumnOperation") { TestDisplayName = "alter column narrows a sized type" },
        new(new AlterColumnOperation
            {
                Table = "questions", Name = "weight", ClrType = typeof(int), IsNullable = false,
                OldColumn = new AddColumnOperation { ClrType = typeof(int), IsNullable = false, DefaultValue = 0 },
            }, "AlterColumnOperation") { TestDisplayName = "alter column removes a default" },
        new(new AddColumnOperation { Table = "questions", Name = "extra", ClrType = typeof(string), IsNullable = false }, "AddColumnOperation") { TestDisplayName = "add non-nullable column without a default" },
        new(new CreateIndexOperation { Table = "questions", Name = "ux_questions_slug", Columns = ["slug"], IsUnique = true }, "CreateIndexOperation") { TestDisplayName = "unique index on existing table" },
        new(new AddForeignKeyOperation
            {
                Table = "questions", Name = "fk_questions_topics", Columns = ["topic_id"], PrincipalTable = "topics",
            }, "AddForeignKeyOperation") { TestDisplayName = "foreign key on existing table" },
        new(new AddCheckConstraintOperation { Table = "questions", Name = "ck_questions_weight", Sql = "weight > 0" }, "AddCheckConstraintOperation") { TestDisplayName = "check constraint on existing table" },
        new(new AddUniqueConstraintOperation { Table = "questions", Name = "uq_questions_slug", Columns = ["slug"] }, "AddUniqueConstraintOperation") { TestDisplayName = "unique constraint on existing table" },
        new(new SqlOperation { Sql = "UPDATE questions SET title = ''" }, "SqlOperation") { TestDisplayName = "raw SQL" },
        new(new AlterColumnOperation
            {
                Table = "questions", Name = "weight", ClrType = typeof(short), IsNullable = true,
                OldColumn = new AddColumnOperation { ClrType = typeof(int), IsNullable = true },
            }, "AlterColumnOperation") { TestDisplayName = "alter column narrows the CLR type only" },
        new(new AddPrimaryKeyOperation { Table = "questions", Name = "pk_questions", Columns = ["id"] }, "AddPrimaryKeyOperation") { TestDisplayName = "primary key on existing table" },
    ];

    [Theory]
    [MemberData(nameof(Forbidden))]
    public void ForbiddenOperation_IsReportedWithMigrationAndOperationName(MigrationOperation operation, string operationName)
    {
        IReadOnlyList<string> violations = ExpandOnlyRule.Violations(Migration, [operation], ExistingTables);

        string violation = Assert.Single(violations);
        Assert.Contains(Migration, violation, StringComparison.Ordinal);
        Assert.Contains(operationName, violation, StringComparison.Ordinal);
    }

    public static IEnumerable<TheoryDataRow<MigrationOperation>> Allowed() =>
    [
        new(new AlterColumnOperation
        {
            Table = "questions", Name = "title", ClrType = typeof(string), IsNullable = true, ColumnType = "text",
            OldColumn = new AddColumnOperation { ClrType = typeof(string), IsNullable = true, ColumnType = "character varying(100)" },
        }) { TestDisplayName = "varchar widened to text" },
        new(new AlterColumnOperation
        {
            Table = "questions", Name = "title", ClrType = typeof(string), IsNullable = true, ColumnType = "character varying(200)",
            OldColumn = new AddColumnOperation { ClrType = typeof(string), IsNullable = true, ColumnType = "character varying(100)" },
        }) { TestDisplayName = "varchar(100) widened to varchar(200)" },
        new(new AlterColumnOperation
        {
            Table = "questions", Name = "weight", ClrType = typeof(long), IsNullable = true,
            OldColumn = new AddColumnOperation { ClrType = typeof(int), IsNullable = true },
        }) { TestDisplayName = "CLR int widened to long" },
        new(new RenameIndexOperation { Table = "questions", Name = "ix_old", NewName = "ix_new" }) { TestDisplayName = "index rename" },
    ];

    [Theory]
    [MemberData(nameof(Allowed))]
    public void AllowedOperation_IsNotReported(MigrationOperation operation) =>
        Assert.Empty(ExpandOnlyRule.Violations(Migration, [operation], ExistingTables));

    [Fact]
    public void DropIndexOnATableCreatedByTheSameMigration_IsNotReported()
    {
        MigrationOperation[] operations =
        [
            new CreateTableOperation { Name = "widgets" },
            new DropIndexOperation { Table = "widgets", Name = "ix_widgets_name" },
        ];

        Assert.Empty(ExpandOnlyRule.Violations(Migration, operations, ExistingTables));
    }

    [Fact]
    public void ExpandOnlyMigration_IsNotReported()
    {
        MigrationOperation[] operations =
        [
            new CreateTableOperation { Name = "widgets" },
            new AddColumnOperation { Table = "questions", Name = "note", ClrType = typeof(string), IsNullable = true },
            new AddColumnOperation { Table = "questions", Name = "weight", ClrType = typeof(int), IsNullable = false, DefaultValue = 0 },
            new CreateIndexOperation { Table = "questions", Name = "ix_questions_note", Columns = ["note"] },
            new AlterColumnOperation
            {
                Table = "questions", Name = "title", ClrType = typeof(string), IsNullable = true, ColumnType = "character varying(200)",
                OldColumn = new AddColumnOperation { ClrType = typeof(string), IsNullable = true, ColumnType = "character varying(100)" },
            },
        ];

        Assert.Empty(ExpandOnlyRule.Violations(Migration, operations, ExistingTables));
    }

    [Fact]
    public void ConstraintsOnATableCreatedByTheSameMigration_AreNotReported()
    {
        MigrationOperation[] operations =
        [
            new CreateTableOperation { Name = "widgets" },
            new CreateIndexOperation { Table = "widgets", Name = "ux_widgets_name", Columns = ["name"], IsUnique = true },
            new AddForeignKeyOperation { Table = "widgets", Name = "fk_widgets_questions", Columns = ["question_id"], PrincipalTable = "questions" },
        ];

        Assert.Empty(ExpandOnlyRule.Violations(Migration, operations, ExistingTables));
    }

    private static List<(string Id, Type Type)> DiscoverMigrations() =>
        typeof(TrainerDbContext).Assembly.GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Id: t.GetCustomAttribute<MigrationAttribute>()?.Id, Type: t))
            .Where(x => x.Id is not null)
            .Select(x => (Id: x.Id!, x.Type))
            .OrderBy(x => x.Id, StringComparer.Ordinal)
            .ToList();

    private static HashSet<string> TablesOf(Type migrationType)
    {
        Migration migration = (Migration)Activator.CreateInstance(migrationType)!;
        return migration.TargetModel.GetEntityTypes()
            .Select(e => e.GetTableName())
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);
    }

    [ContractMigration("synthetic")]
    private sealed class MarkedContractStep;

    private sealed class UnmarkedStep;
}
