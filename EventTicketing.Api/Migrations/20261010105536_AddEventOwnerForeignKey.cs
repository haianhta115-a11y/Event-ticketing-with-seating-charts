using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddEventOwnerForeignKey : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_events_owner_user_id",
                table: "events",
                column: "owner_user_id");

            migrationBuilder.AddForeignKey(
                name: "FK_events_Users_owner_user_id",
                table: "events",
                column: "owner_user_id",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_events_Users_owner_user_id",
                table: "events");

            migrationBuilder.DropIndex(
                name: "IX_events_owner_user_id",
                table: "events");
        }
    }
}
