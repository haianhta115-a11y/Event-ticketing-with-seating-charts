using EventTicketing.Api.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class ActivationMigrationTests
{
    [Fact]
    public void Migration_OnlyAddsActivationSchema_AndNeverChangesAccountsOrCredentials()
    {
        var migration = new AddAccountActivations();
        var table = Assert.Single(migration.UpOperations.OfType<CreateTableOperation>());
        Assert.Equal("account_activations", table.Name);
        Assert.Equal("Users", Assert.Single(table.ForeignKeys).PrincipalTable);
        Assert.Equal(2, migration.UpOperations.OfType<CreateIndexOperation>().Count());
        Assert.All(migration.UpOperations.OfType<CreateIndexOperation>(), index => Assert.True(index.IsUnique));
        Assert.Equal(3, migration.UpOperations.Count);
        Assert.Equal("account_activations", Assert.IsType<DropTableOperation>(Assert.Single(migration.DownOperations)).Name);
    }
}
