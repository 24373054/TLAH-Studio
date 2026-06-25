using Microsoft.EntityFrameworkCore;
using TLAHStudio.Data;

namespace TLAHStudio.Core.Tests;

internal static class TestDb
{
    public static TlahDbContext Create()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "TLAHStudio.Tests", $"{Guid.NewGuid():N}.db");
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        var options = new DbContextOptionsBuilder<TlahDbContext>()
            .UseSqlite($"Data Source={dbPath}")
            .Options;
        var db = new TlahDbContext(options);
        db.Initialize();
        return db;
    }
}
