using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenBP.Connectors.PsySupport.Data;

/// <summary>Для <c>dotnet ef</c>. Рядок підключення — зі змінної <c>ConnectionStrings__PsySupport</c> або локальний dev-postgres.</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<PsyDbContext>
{
    public PsyDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("ConnectionStrings__PsySupport")
                 ?? "Host=localhost;Port=5432;Database=psy_support;Username=psy;Password=psy";
        return new PsyDbContext(new DbContextOptionsBuilder<PsyDbContext>().UseNpgsql(cs).Options);
    }
}
