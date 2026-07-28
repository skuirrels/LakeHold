using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Lakehold.ControlPlane.Data;

/// <summary>Creates the PostgreSQL model for EF tooling without booting the API.</summary>
public sealed class ControlPlaneDesignTimeFactory : IDesignTimeDbContextFactory<ControlPlaneContext>
{
    public ControlPlaneContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<ControlPlaneContext>()
            .UseNpgsql(
                "Host=localhost;Database=lakehold_control;Username=lakehold;Password=design-time-only",
                npgsql => npgsql.MigrationsAssembly(typeof(ControlPlaneContext).Assembly.GetName().Name!))
            .Options;

        return new ControlPlaneContext(options);
    }
}
