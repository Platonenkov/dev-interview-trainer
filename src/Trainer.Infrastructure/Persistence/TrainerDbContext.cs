using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence;

/// <summary>
/// The application database. It also stores the data protection key ring (ADR 0004), which the host registers with
/// <c>PersistKeysToDbContext</c>.
/// </summary>
public sealed class TrainerDbContext(DbContextOptions<TrainerDbContext> options) : DbContext(options), IDataProtectionKeyContext
{
    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    internal DbSet<StackEntity> Stacks => Set<StackEntity>();
    internal DbSet<StackLocaleEntity> StackLocales => Set<StackLocaleEntity>();
    internal DbSet<TopicEntity> Topics => Set<TopicEntity>();
    internal DbSet<TopicLocaleEntity> TopicLocales => Set<TopicLocaleEntity>();
    internal DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    internal DbSet<QuestionLocaleEntity> QuestionLocales => Set<QuestionLocaleEntity>();
    internal DbSet<RedirectEntity> Redirects => Set<RedirectEntity>();
    internal DbSet<ContentImportEntity> ContentImports => Set<ContentImportEntity>();
    internal DbSet<UserEntity> Users => Set<UserEntity>();
    internal DbSet<UserRoleEntity> UserRoles => Set<UserRoleEntity>();
    internal DbSet<AnswerEntity> Answers => Set<AnswerEntity>();
    internal DbSet<ReviewStateEntity> ReviewStates => Set<ReviewStateEntity>();
    internal DbSet<MockSessionEntity> MockSessions => Set<MockSessionEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TrainerDbContext).Assembly);
    }
}
