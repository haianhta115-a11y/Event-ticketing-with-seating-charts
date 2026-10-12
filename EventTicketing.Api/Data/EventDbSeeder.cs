namespace EventTicketing.Api.Data;

// Legacy entry points retained to prevent accidental execution by old callers.
// Provision accounts and fixtures explicitly in an isolated local database.
public static class EventDbSeeder
{
    public static void EnsureAdminUserAndRoles(AppDbContext db) =>
        throw new InvalidOperationException("Legacy credential seeding is disabled.");

    public static void Seed(AppDbContext db) =>
        throw new InvalidOperationException("Destructive development seeding is disabled.");
}
