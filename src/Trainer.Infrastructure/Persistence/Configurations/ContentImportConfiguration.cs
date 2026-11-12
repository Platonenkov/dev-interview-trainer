using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class ContentImportConfiguration : IEntityTypeConfiguration<ContentImportEntity>
{
    public void Configure(EntityTypeBuilder<ContentImportEntity> builder)
    {
        builder.ToTable("content_imports");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).UseIdentityByDefaultColumn();
        builder.Property(i => i.ImportedAt).HasColumnType("timestamp with time zone");

        // The hash is not unique: rolling back to earlier content (A, B, A) inserts a second row for A.
        builder.HasIndex(i => i.Hash);

        // Exactly one row may be active; the importer flips the previous one in the same transaction.
        builder.HasIndex(i => i.IsActive).IsUnique().HasFilter("is_active");
    }
}
