using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ChunkMaestro.Persistence;

// Design-time model generation only; it does not open or initialize any database.
internal sealed class FleetDesignFactory : IDesignTimeDbContextFactory<FleetDbContext>
{
    public FleetDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<FleetDbContext>().UseSqlite("Data Source=:memory:").Options);
}
