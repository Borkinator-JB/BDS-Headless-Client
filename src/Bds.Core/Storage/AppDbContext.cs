using Microsoft.EntityFrameworkCore;

namespace Bds.Core.Storage;

public sealed class ServerEntry
{
    public int Id { get; set; }
    public required string Name { get; set; }
    public required string Host { get; set; }
    public int Port { get; set; } = 19132;

    /// <summary>Address friends get transferred to. Needed when Host is a LAN or localhost address.</summary>
    public string? PublicHost { get; set; }
    public int? PublicPort { get; set; }
    public bool IsActive { get; set; }
}

public sealed class SettingEntry
{
    public required string Key { get; set; }
    public required string Value { get; set; }
}

public sealed class ActivityEntry
{
    public long Id { get; set; }
    public DateTimeOffset Time { get; set; } = DateTimeOffset.UtcNow;
    public required string Kind { get; set; }
    public required string Message { get; set; }
}

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<ServerEntry> Servers => Set<ServerEntry>();
    public DbSet<SettingEntry> Settings => Set<SettingEntry>();
    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<ServerEntry>(e =>
        {
            e.Property(x => x.Name).HasMaxLength(100);
            e.Property(x => x.Host).HasMaxLength(255);
            e.Property(x => x.PublicHost).HasMaxLength(255);
        });
        b.Entity<SettingEntry>().HasKey(x => x.Key);
        b.Entity<ActivityEntry>(e =>
        {
            e.HasIndex(x => x.Time);
            // SQLite can't order by DateTimeOffset natively.
            e.Property(x => x.Time).HasConversion(v => v.ToUnixTimeMilliseconds(), v => DateTimeOffset.FromUnixTimeMilliseconds(v));
        });
    }

    public static DbContextOptions<AppDbContext> SqliteOptions(string path) =>
        new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
}
