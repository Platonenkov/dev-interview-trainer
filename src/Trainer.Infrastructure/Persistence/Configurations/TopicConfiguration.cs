using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Trainer.Infrastructure.Persistence.Entities;

namespace Trainer.Infrastructure.Persistence.Configurations;

internal sealed class TopicConfiguration : IEntityTypeConfiguration<TopicEntity>
{
    public void Configure(EntityTypeBuilder<TopicEntity> builder)
    {
        builder.ToTable("topics");
        builder.HasKey(t => new { t.StackKey, t.Key });
        builder.Property(t => t.RetiredAt).HasColumnType("timestamp with time zone");
        builder.HasOne<StackEntity>().WithMany().HasForeignKey(t => t.StackKey).OnDelete(DeleteBehavior.Cascade);
    }
}

internal sealed class TopicLocaleConfiguration : IEntityTypeConfiguration<TopicLocaleEntity>
{
    public void Configure(EntityTypeBuilder<TopicLocaleEntity> builder)
    {
        builder.ToTable("topic_locales");
        builder.HasKey(l => new { l.StackKey, l.TopicKey, l.Culture });
        builder.HasOne<TopicEntity>().WithMany().HasForeignKey(l => new { l.StackKey, l.TopicKey }).OnDelete(DeleteBehavior.Cascade);
    }
}
