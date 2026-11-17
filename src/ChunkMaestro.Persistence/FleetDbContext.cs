using Microsoft.EntityFrameworkCore;

namespace ChunkMaestro.Persistence;

internal sealed class FleetRow
{
    public Guid Id
    {
        get; set;
    }
    public string Name
    {
        get; set;
    } = "";
}

internal sealed class HostRow
{
    public Guid Id
    {
        get; set;
    }

    public Guid FleetId
    {
        get; set;
    }

    public string Name
    {
        get; set;
    } = "";
    public string Address
    {
        get; set;
    } = "";
    public long RegisteredAt
    {
        get; set;
    }

}
internal sealed class InstanceRow
{
    public Guid Id
    {
        get; set;
    }

    public Guid HostId
    {
        get; set;
    }

    public string Name
    {
        get; set;
    } = "";
    public string ServiceName
    {
        get; set;
    } = "";
    public long RegisteredAt
    {
        get; set;
    }

}
internal sealed class EndpointRow
{
    public Guid InstanceId
    {
        get; set;
    }

    public Guid HostId
    {
        get; set;
    }

    public int Edition
    {
        get; set;
    }

    public string Address
    {
        get; set;
    } = "";
    public int Port
    {
        get; set;
    }

}
internal sealed class FleetDbContext(DbContextOptions<FleetDbContext> options) : DbContext(options)
{
    public DbSet<FleetRow> Fleets => Set<FleetRow>();
    public DbSet<HostRow> Hosts => Set<HostRow>();
    public DbSet<InstanceRow> Instances => Set<InstanceRow>();
    public DbSet<EndpointRow> Endpoints => Set<EndpointRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FleetRow>(e =>
{
    e.ToTable("Fleets");
    e.HasKey(x => x.Id);
    e.Property(x => x.Name).IsRequired();
});
        modelBuilder.Entity<HostRow>(e =>
        {
            e.ToTable("Hosts");
            e.HasKey(x => x.Id);
            e.HasOne<FleetRow>().WithMany().HasForeignKey(x => x.FleetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new
            {
                x.FleetId,
                x.Address
            }).IsUnique();
        });
        modelBuilder.Entity<InstanceRow>(e =>
        {
            e.ToTable("Instances");
            e.HasKey(x => x.Id);
            e.HasOne<HostRow>().WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new
            {
                x.HostId,
                x.ServiceName
            }).IsUnique();
        });
        modelBuilder.Entity<EndpointRow>(e =>
        {
            e.ToTable("Endpoints", table =>
            {
                table.HasCheckConstraint("CK_Endpoints_Port", "typeof(\"Port\") = 'integer' AND \"Port\" BETWEEN 1 AND 65535");
                table.HasCheckConstraint("CK_Endpoints_Edition", "\"Edition\" IN (0, 1)");
            });
            e.HasKey(x => new
            {
                x.InstanceId,
                x.Edition,
                x.Address,
                x.Port
            });
            e.HasOne<InstanceRow>().WithMany().HasForeignKey(x => x.InstanceId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<HostRow>().WithMany().HasForeignKey(x => x.HostId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => new
            {
                x.HostId,
                x.Edition,
                x.Port
            }).IsUnique();
        });
    }
}
