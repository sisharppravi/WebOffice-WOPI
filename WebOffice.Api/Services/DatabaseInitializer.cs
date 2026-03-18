using bsckend.Repository;
using Microsoft.EntityFrameworkCore;

namespace bsckend.Services;

public static class DatabaseInitializer
{
    public static async Task InitializeAsync(ApplicationDbContext context)
    {
        try
        {
            // Применить миграции
            await context.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Database migration failed: {ex.Message}");
            throw;
        }
    }
}

