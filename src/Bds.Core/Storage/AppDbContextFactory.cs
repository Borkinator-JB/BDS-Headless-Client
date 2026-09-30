using Microsoft.EntityFrameworkCore.Design;

namespace Bds.Core.Storage;

/// <summary>Used by dotnet-ef at design time.</summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args) => new(AppDbContext.SqliteOptions("design.db"));
}
