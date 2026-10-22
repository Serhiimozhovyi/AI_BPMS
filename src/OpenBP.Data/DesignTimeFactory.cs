using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace OpenBP.Data;

/// <summary>Для <c>dotnet ef</c>. Рядок підключення — зі змінної <c>OPENBP_DB</c> (за замовчуванням — локальний dev-postgres).</summary>
public sealed class DesignTimeFactory : IDesignTimeDbContextFactory<OpenBpDbContext>
{
    public OpenBpDbContext CreateDbContext(string[] args)
    {
        var cs = Environment.GetEnvironmentVariable("OPENBP_DB")
                 ?? "Host=localhost;Port=5432;Database=openbp;Username=openbp;Password=openbp";
        var options = new DbContextOptionsBuilder<OpenBpDbContext>().UseNpgsql(cs).Options;
        return new OpenBpDbContext(options);
    }
}
