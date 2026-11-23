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

    public DbSet<ControllerIdentityRow> ControllerIdentities => Set<ControllerIdentityRow>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<ControllerIdentityRow>(e =>
        {
            e.ToTable("ControllerIdentities", table =>
            {
                table.HasCheckConstraint("CK_ControllerIdentity_Profile", "typeof(ProfileVersion) = 'integer' AND ProfileVersion = 1 AND Purpose = 'controller-root-v1'");
                table.HasCheckConstraint("CK_ControllerIdentity_Material", "typeof(CertificateDer) = 'blob' AND length(CertificateDer) BETWEEN 1 AND 4096 AND typeof(CertificateHash) = 'blob' AND length(CertificateHash) = 32 AND typeof(SpkiHash) = 'blob' AND length(SpkiHash) = 32");
                table.HasCheckConstraint("CK_ControllerIdentity_StateTime", "typeof(State) = 'integer' AND typeof(CreatedAt) = 'integer' AND CreatedAt BETWEEN -62135596800000 AND 253402300799999 AND ((State = 0 AND PublishedAt IS NULL) OR (State = 1 AND typeof(PublishedAt) = 'integer' AND PublishedAt BETWEEN CreatedAt AND 253402300799999))");
                table.HasCheckConstraint("CK_ControllerIdentity_FleetId", "typeof(FleetId) = 'text' AND length(FleetId) = 36 AND FleetId <> '00000000-0000-0000-0000-000000000000' AND substr(FleetId,9,1) = '-' AND substr(FleetId,14,1) = '-' AND substr(FleetId,19,1) = '-' AND substr(FleetId,24,1) = '-' AND length(replace(FleetId,'-','')) = 32 AND replace(FleetId,'-','') NOT GLOB '*[^0-9A-F]*'");
                table.HasCheckConstraint("CK_ControllerIdentity_ControllerId", "typeof(ControllerId) = 'text' AND length(ControllerId) = 36 AND ControllerId <> '00000000-0000-0000-0000-000000000000' AND substr(ControllerId,9,1) = '-' AND substr(ControllerId,14,1) = '-' AND substr(ControllerId,19,1) = '-' AND substr(ControllerId,24,1) = '-' AND length(replace(ControllerId,'-','')) = 32 AND replace(ControllerId,'-','') NOT GLOB '*[^0-9A-F]*'");
                table.HasCheckConstraint("CK_ControllerIdentity_AttemptId", "typeof(AttemptId) = 'text' AND length(AttemptId) = 36 AND AttemptId <> '00000000-0000-0000-0000-000000000000' AND substr(AttemptId,9,1) = '-' AND substr(AttemptId,14,1) = '-' AND substr(AttemptId,19,1) = '-' AND substr(AttemptId,24,1) = '-' AND length(replace(AttemptId,'-','')) = 32 AND replace(AttemptId,'-','') NOT GLOB '*[^0-9A-F]*'");
                table.HasCheckConstraint("CK_ControllerIdentity_CredentialId", "typeof(CredentialId) = 'text' AND length(CredentialId) = 36 AND CredentialId <> '00000000-0000-0000-0000-000000000000' AND substr(CredentialId,9,1) = '-' AND substr(CredentialId,14,1) = '-' AND substr(CredentialId,19,1) = '-' AND substr(CredentialId,24,1) = '-' AND length(replace(CredentialId,'-','')) = 32 AND replace(CredentialId,'-','') NOT GLOB '*[^0-9A-F]*'");
            });
            e.HasKey(x => x.FleetId);
            e.HasOne<FleetRow>().WithMany().HasForeignKey(x => x.FleetId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(x => x.ControllerId).IsUnique();
            e.HasIndex(x => x.AttemptId).IsUnique();
            e.HasIndex(x => x.CredentialId).IsUnique();
        });
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
