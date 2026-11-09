using System.Text;
using Microsoft.EntityFrameworkCore;

namespace Analytics.Postgres;

// Persistence entities. They stay inside this project; the rest of the code base only sees the Core records.

public sealed class SiteEntity
{
    public long Id { get; set; }
    public string Domain { get; set; } = "";
    public string? Name { get; set; }
    public DateTime CreatedAt { get; set; }
    public bool IdentifyUsers { get; set; }
}

public sealed class ApiKeyEntity
{
    public long Id { get; set; }
    public string Name { get; set; } = "";
    public string Scope { get; set; } = "read"; // 'read' or 'admin'
    public byte[] KeyHash { get; set; } = [];
    public DateTime CreatedAt { get; set; }
    public DateTime? LastUsedAt { get; set; }
}

public sealed class DailySaltEntity
{
    public DateOnly Day { get; set; }
    public byte[] Salt { get; set; } = [];
}

/// <summary>
/// Mapped for the schema and for bulk deletes only. Events are written with binary COPY (see PgEventStore),
/// never through the change tracker.
/// </summary>
public sealed class EventEntity
{
    public long Id { get; set; }
    public DateTime Ts { get; set; }
    public long SiteId { get; set; }
    public string Visitor { get; set; } = "";
    public string Type { get; set; } = "";   // 'pageview' or a custom event name
    public string Path { get; set; } = "";
    public string? Referrer { get; set; }    // host only
    public string? Country { get; set; }     // ISO 3166-1 alpha-2
    public string? Browser { get; set; }
    public string? Os { get; set; }
    public string? Device { get; set; }
    public string? UtmSource { get; set; }
    public string? UtmMedium { get; set; }
    public string? UtmCampaign { get; set; }
    public string? Props { get; set; }       // jsonb
}

public sealed class AnalyticsDbContext(DbContextOptions<AnalyticsDbContext> options) : DbContext(options)
{
    public DbSet<SiteEntity> Sites => Set<SiteEntity>();
    public DbSet<ApiKeyEntity> ApiKeys => Set<ApiKeyEntity>();
    public DbSet<DailySaltEntity> DailySalts => Set<DailySaltEntity>();
    public DbSet<EventEntity> Events => Set<EventEntity>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.UseIdentityAlwaysColumns();

        // Sites that events can be recorded for. The public identifier used by the tracking script is the domain.
        b.Entity<SiteEntity>(e =>
        {
            e.ToTable("sites");
            e.HasKey(x => x.Id).HasName("sites_pkey");
            e.HasIndex(x => x.Domain).IsUnique().HasDatabaseName("sites_domain_key");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        // API keys for the query/admin API. Only a SHA-256 hash of the key is stored.
        b.Entity<ApiKeyEntity>(e =>
        {
            e.ToTable("api_keys", t => t.HasCheckConstraint("api_keys_scope_check", "scope IN ('read', 'admin')"));
            e.HasKey(x => x.Id).HasName("api_keys_pkey");
            e.HasIndex(x => x.KeyHash).IsUnique().HasDatabaseName("api_keys_key_hash_key");
            e.Property(x => x.Scope).HasDefaultValue("read");
            e.Property(x => x.CreatedAt).HasDefaultValueSql("now()");
        });

        // One salt per UTC day for visitor hashing, sealed with AES-GCM under Ingest:SaltKey (which never reaches
        // the database), so a dump or backup alone cannot recompute visitor ids. Old rows are deleted by the
        // ingest service, which is what makes visitor identifiers unlinkable across days.
        b.Entity<DailySaltEntity>(e =>
        {
            e.ToTable("daily_salts");
            e.HasKey(x => x.Day).HasName("daily_salts_pkey");
            e.Property(x => x.Day).ValueGeneratedNever();
        });

        // site_id deliberately has no foreign key: it keeps the insert path cheap, and the API deletes a site's
        // events together with the site.
        b.Entity<EventEntity>(e =>
        {
            e.ToTable("events");
            e.HasKey(x => x.Id).HasName("events_pkey");
            e.Property(x => x.Props).HasColumnType("jsonb");
            e.HasIndex(x => new { x.SiteId, x.Ts }).HasDatabaseName("events_site_ts");
            e.HasIndex(x => new { x.SiteId, x.Type, x.Ts }).HasDatabaseName("events_site_type_ts");
        });

        // snake_case column names (CreatedAt -> created_at), matching the raw SQL used by COPY and the stats queries.
        foreach (var entity in b.Model.GetEntityTypes())
        foreach (var property in entity.GetProperties())
        {
            property.SetColumnName(ToSnakeCase(property.Name));
        }
    }

    private static string ToSnakeCase(string name)
    {
        var sb = new StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) sb.Append('_');
            sb.Append(char.ToLowerInvariant(name[i]));
        }
        return sb.ToString();
    }
}
