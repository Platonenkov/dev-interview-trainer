using Microsoft.EntityFrameworkCore;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence;

public sealed class TrainerDbContext(DbContextOptions<TrainerDbContext> options) : DbContext(options)
{
    internal DbSet<StackEntity> Stacks => Set<StackEntity>();
    internal DbSet<StackLocaleEntity> StackLocales => Set<StackLocaleEntity>();
    internal DbSet<TopicEntity> Topics => Set<TopicEntity>();
    internal DbSet<TopicLocaleEntity> TopicLocales => Set<TopicLocaleEntity>();
    internal DbSet<QuestionEntity> Questions => Set<QuestionEntity>();
    internal DbSet<QuestionLocaleEntity> QuestionLocales => Set<QuestionLocaleEntity>();
    internal DbSet<RedirectEntity> Redirects => Set<RedirectEntity>();
    internal DbSet<ContentImportEntity> ContentImports => Set<ContentImportEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TrainerDbContext).Assembly);
    }
}
