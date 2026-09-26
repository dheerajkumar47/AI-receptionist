using AiReceptionist.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace AiReceptionist.Core.Data;

/// <summary>EF Core context (SQLite by default). Always obtained through <see cref="IDbContextFactory{TContext}"/>
/// so background workers and Blazor circuits never share an instance.</summary>
public class ReceptionistDbContext : DbContext
{
    public ReceptionistDbContext(DbContextOptions<ReceptionistDbContext> options) : base(options) { }

    public DbSet<Contact> Contacts => Set<Contact>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<IntentDefinition> Intents => Set<IntentDefinition>();
    public DbSet<ConversationRule> Rules => Set<ConversationRule>();
    public DbSet<BotSettings> Settings => Set<BotSettings>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<ChannelCursor> ChannelCursors => Set<ChannelCursor>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<Contact>(e =>
        {
            e.HasIndex(x => new { x.Channel, x.ExternalUserId }).IsUnique();
            e.Property(x => x.Channel).HasMaxLength(32);
            e.Property(x => x.ExternalUserId).HasMaxLength(128);
        });

        b.Entity<Conversation>(e =>
        {
            e.HasOne(x => x.Contact).WithMany(x => x.Conversations).HasForeignKey(x => x.ContactId);
            e.HasIndex(x => x.LastActivityUtc);
            e.Property(x => x.Mode).HasConversion<string>();
        });

        b.Entity<Message>(e =>
        {
            e.HasOne(x => x.Conversation).WithMany(x => x.Messages).HasForeignKey(x => x.ConversationId);
            e.HasIndex(x => new { x.Channel, x.ExternalId }).IsUnique().HasFilter("ExternalId IS NOT NULL");
            e.HasIndex(x => x.CreatedUtc);
            e.Property(x => x.Direction).HasConversion<string>();
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.ReplyMode).HasConversion<string>();
        });

        b.Entity<IntentDefinition>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.Property(x => x.Action).HasConversion<string>();
            e.Property(x => x.ReplyMode).HasConversion<string>();
        });

        b.Entity<ConversationRule>(e =>
        {
            e.Property(x => x.MatchType).HasConversion<string>();
            e.Property(x => x.Action).HasConversion<string>();
        });

        b.Entity<BotSettings>(e => e.Property(x => x.DefaultReplyMode).HasConversion<string>());

        b.Entity<Appointment>(e =>
        {
            e.HasOne(x => x.Conversation).WithMany(x => x.Appointments).HasForeignKey(x => x.ConversationId);
            e.HasIndex(x => x.StartUtc);
            e.Property(x => x.Status).HasConversion<string>();
            e.Property(x => x.Method).HasConversion<string>();
        });

        b.Entity<ChannelCursor>().HasKey(x => x.Channel);
    }
}
