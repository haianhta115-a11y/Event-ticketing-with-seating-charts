using EventTicketing.Api.Migrations;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;

namespace EventTicketing.Tests;

[Trait("Category", "Unit")]
public sealed class EventOwnerMigrationTests
{
    [Fact]
    public void OwnerMigration_OnlyAddsIndexAndRestrictiveForeignKey_NoDataChanges()
    {
        var migration = new AddEventOwnerForeignKey();
        Assert.Equal(2, migration.UpOperations.Count);
        var index = Assert.Single(migration.UpOperations.OfType<CreateIndexOperation>());
        Assert.Equal("events", index.Table);
        Assert.Equal("owner_user_id", Assert.Single(index.Columns));
        var fk = Assert.Single(migration.UpOperations.OfType<AddForeignKeyOperation>());
        Assert.Equal("Users", fk.PrincipalTable);
        Assert.Equal(ReferentialAction.Restrict, fk.OnDelete);
        Assert.Equal(2, migration.DownOperations.Count);
        Assert.Single(migration.DownOperations.OfType<DropForeignKeyOperation>());
        Assert.Single(migration.DownOperations.OfType<DropIndexOperation>());
    }
}
