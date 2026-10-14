using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using EventTicketing.Api.Data;
using EventTicketing.Api.Entities;
using EventTicketing.Api.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Metadata;
using Npgsql;

namespace EventTicketing.LocalSetup;

// Explicit CLI only. Never invoked by application startup; never migrates an existing DB.
public static class FreshLocalDatabase
{
    public static NpgsqlConnectionStringBuilder Validate(string connectionString, string target)
    {
        NpgsqlConnectionStringBuilder connection;
        try { connection = new(connectionString); }
        catch { throw new InvalidOperationException("Invalid local connection configuration; values withheld."); }
        if (!Regex.IsMatch(target, @"\Aevent_ticketing_test_team_[a-f0-9]{32}\z", RegexOptions.CultureInvariant)
            || connection.Database != target || connection.Host != "127.0.0.1" || connection.Port != 5432
            || string.IsNullOrWhiteSpace(connection.Username))
            throw new InvalidOperationException("Requires exact new team test database, literal 127.0.0.1:5432 and explicit username.");
        connection.Pooling = false;
        connection.IncludeErrorDetail = false;
        connection.LogParameters = false;
        return connection;
    }

    public static AppDbContext Context(string connection) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection).Options);

    public static IReadOnlyList<MigrationOperation> SchemaOperations(AppDbContext context)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var operations = context.GetService<IMigrationsModelDiffer>().GetDifferences(null, model.GetRelationalModel())
            .Where(operation => operation is not InsertDataOperation and not UpdateDataOperation and not DeleteDataOperation).ToList();
        if (operations.Any(operation => operation is not CreateTableOperation and not CreateIndexOperation
            and not CreateSequenceOperation and not AlterDatabaseOperation))
            throw new InvalidOperationException("Unexpected schema operation; initializer refuses this model.");
        return operations;
    }

    private static string ModelHash(AppDbContext context)
    {
        var model = context.GetService<IDesignTimeModel>().Model;
        var commands = context.GetService<IMigrationsSqlGenerator>().Generate(SchemaOperations(context), model);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join("\n", commands.Select(c => c.CommandText)))));
    }

    private static async Task Identity(NpgsqlConnection connection, string target)
    {
        await using var query = new NpgsqlCommand("SELECT current_database(), inet_server_port()", connection);
        await using var reader = await query.ExecuteReaderAsync();
        if (!await reader.ReadAsync() || reader.GetString(0) != target || reader.GetInt32(1) != 5432)
            throw new InvalidOperationException("Local PostgreSQL identity mismatch.");
    }

    public static async Task Initialize(string connectionString, string target)
    {
        var verified = Validate(connectionString, target); // All guards BEFORE any connection.
        await using var context = Context(verified.ConnectionString);
        var operations = SchemaOperations(context); // Inspect model BEFORE CREATE DATABASE.
        var hash = ModelHash(context);
        var adminString = new NpgsqlConnectionStringBuilder(verified.ConnectionString) { Database = "postgres" };
        await using (var admin = new NpgsqlConnection(adminString.ConnectionString))
        {
            await admin.OpenAsync();
            await Identity(admin, "postgres");
            await using var exists = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_database WHERE datname=@name)", admin);
            exists.Parameters.AddWithValue("name", target);
            if ((bool)(await exists.ExecuteScalarAsync())!)
                throw new InvalidOperationException("Target already exists; no initialization or overwrite is allowed.");
            // Identifier validated to an exact safe alphabet above. CREATE itself refuses races.
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{target}\" TEMPLATE template0", admin);
            await create.ExecuteNonQueryAsync();
        }

        await context.Database.OpenConnectionAsync();
        var local = (NpgsqlConnection)context.Database.GetDbConnection();
        await Identity(local, target);
        await using var empty = new NpgsqlCommand("""
            SELECT (SELECT count(*) FROM pg_class c JOIN pg_namespace n ON n.oid=c.relnamespace
                    WHERE n.nspname NOT LIKE 'pg_%' AND n.nspname <> 'information_schema')
                 + (SELECT count(*) FROM pg_namespace WHERE nspname NOT LIKE 'pg_%'
                    AND nspname NOT IN ('public','information_schema'))
            """, local);
        if ((long)(await empty.ExecuteScalarAsync())! != 0)
            throw new InvalidOperationException("New target is not empty. Stopped; target preserved.");

        await using var transaction = await context.Database.BeginTransactionAsync();
        var model = context.GetService<IDesignTimeModel>().Model;
        foreach (var command in context.GetService<IMigrationsSqlGenerator>().Generate(operations, model))
            await context.Database.ExecuteSqlRawAsync(command.CommandText);
        // Only the existing role definitions; NEVER historical HasData users/passwords.
        context.Roles.AddRange(new Role { Name = "Buyer" }, new Role { Name = "Organizer" },
            new Role { Name = "GateStaff" }, new Role { Name = "Accountant" }, new Role { Name = "Admin" });
        await context.SaveChangesAsync();
        await context.Database.ExecuteSqlRawAsync("""
            CREATE TABLE public.local_setup_metadata
            (database_name text PRIMARY KEY, model_sha256 text NOT NULL, initialized_at timestamptz NOT NULL DEFAULT now())
            """);
        await context.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO public.local_setup_metadata(database_name,model_sha256) VALUES ({target},{hash})");
        await transaction.CommitAsync();
        await Verify(verified.ConnectionString, target);
        // Any failure preserves the new database and evidence; never drops or retries cleanup.
    }

    public static async Task<string> Verify(string connectionString, string target)
    {
        var verified = Validate(connectionString, target);
        await using var context = Context(verified.ConnectionString);
        await context.Database.OpenConnectionAsync();
        var connection = (NpgsqlConnection)context.Database.GetDbConnection();
        await Identity(connection, target);
        await using var marker = new NpgsqlCommand("SELECT model_sha256 FROM public.local_setup_metadata WHERE database_name=@name", connection);
        marker.Parameters.AddWithValue("name", target);
        if ((string?)await marker.ExecuteScalarAsync() != ModelHash(context))
            throw new InvalidOperationException("Fresh local setup marker/model mismatch; do not alter this database.");
        var model = context.GetService<IDesignTimeModel>().Model.GetRelationalModel();
        foreach (var table in model.Tables)
        {
            await using var columns = new NpgsqlCommand("SELECT column_name,is_nullable FROM information_schema.columns WHERE table_schema='public' AND table_name=@name ORDER BY column_name", connection);
            columns.Parameters.AddWithValue("name", table.Name);
            var actual = new Dictionary<string, bool>();
            await using (var reader = await columns.ExecuteReaderAsync())
                while (await reader.ReadAsync()) actual.Add(reader.GetString(0), reader.GetString(1) == "YES");
            if (actual.Count != table.Columns.Count() || table.Columns.Any(c => !actual.TryGetValue(c.Name, out var nullable) || nullable != c.IsNullable))
                throw new InvalidOperationException("Required table/column/nullability mismatch: " + table.Name);
            foreach (var name in table.Indexes.Select(i => i.Name).Concat(table.UniqueConstraints.Select(c => c.Name))
                .Concat(table.ForeignKeyConstraints.Select(c => c.Name)).Concat(table.CheckConstraints.Select(c => c.Name)))
            {
                await using var constraint = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_class WHERE relname=@name) OR EXISTS(SELECT 1 FROM pg_constraint WHERE conname=@name)", connection);
                constraint.Parameters.AddWithValue("name", name!);
                if (!(bool)(await constraint.ExecuteScalarAsync())!) throw new InvalidOperationException("Required schema object missing: " + name);
            }
        }
        foreach (var name in new[] { "Buyer", "Organizer", "Admin" })
            if (await context.Roles.CountAsync(r => r.Name == name) != 1) throw new InvalidOperationException("Required unique role missing: " + name);
        await using var inventory = new NpgsqlCommand("""
            SELECT json_build_object(
              'columns',(SELECT json_agg(row_to_json(x)) FROM (SELECT table_name,column_name,udt_name,is_nullable,column_default FROM information_schema.columns WHERE table_schema='public' ORDER BY table_name,ordinal_position) x),
              'constraints',(SELECT json_agg(row_to_json(x)) FROM (SELECT c.conname,pg_get_constraintdef(c.oid) definition FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace WHERE n.nspname='public' ORDER BY c.conname) x),
              'indexes',(SELECT json_agg(row_to_json(x)) FROM (SELECT tablename,indexname,indexdef FROM pg_indexes WHERE schemaname='public' ORDER BY tablename,indexname) x),
              'sequences',(SELECT json_agg(row_to_json(x)) FROM (SELECT sequencename,data_type,start_value,min_value,max_value,increment_by,cycle FROM pg_sequences WHERE schemaname='public' ORDER BY sequencename) x))::text
            """, connection);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes((string)(await inventory.ExecuteScalarAsync())!)));
    }

    public static async Task Provision(string connectionString, string target, string email, string role, string password)
    {
        if (role is not ("Organizer" or "Admin") || !Regex.IsMatch(email, @"\A[A-Za-z0-9._+-]+@example\.test\z") || password.Length < 12)
            throw new InvalidOperationException("Fixture requires Organizer/Admin, @example.test email and a password of at least 12 characters.");
        await Verify(connectionString, target); // Only databases initialized by this exact model/tool.
        await using var context = Context(Validate(connectionString, target).ConnectionString);
        var normalized = email.ToLowerInvariant();
        await using var transaction = await context.Database.BeginTransactionAsync();
        await context.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({normalized},0))");
        if (await context.Users.AnyAsync(u => u.Email.ToLower() == normalized || u.Username == normalized))
            throw new InvalidOperationException("Fixture account already exists; no password/role overwrite allowed.");
        var definition = await context.Roles.SingleAsync(r => r.Name == role);
        var user = new User
        {
            Email = normalized,
            Username = normalized,
            Name = "Local " + role,
            PasswordHash = PasswordHasher.HashPassword(password),
            Status = "Active"
        };
        user.UserRoles.Add(new UserRole { RoleId = definition.Id });
        context.Users.Add(user);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
