using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace EventTicketing.Api.Migrations
{
    /// <inheritdoc />
    public partial class FixArchitecture_LinkSeatsToShowtime : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_seats_seat_categories_performance_id_seat_category_id",
                table: "seats");

            migrationBuilder.DropTable(
                name: "performances");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_seat_categories_performance_id_id",
                table: "seat_categories");

            migrationBuilder.RenameColumn(
                name: "performance_id",
                table: "seats",
                newName: "showtime_id");

            migrationBuilder.RenameIndex(
                name: "IX_seats_performance_id_status_held_until",
                table: "seats",
                newName: "IX_seats_showtime_id_status_held_until");

            migrationBuilder.RenameIndex(
                name: "IX_seats_performance_id_seat_category_id",
                table: "seats",
                newName: "IX_seats_showtime_id_seat_category_id");

            migrationBuilder.RenameIndex(
                name: "IX_seats_performance_id_row_number",
                table: "seats",
                newName: "IX_seats_showtime_id_row_number");

            migrationBuilder.RenameColumn(
                name: "performance_id",
                table: "seat_categories",
                newName: "showtime_id");

            migrationBuilder.RenameIndex(
                name: "IX_seat_categories_performance_id_normalized_name",
                table: "seat_categories",
                newName: "IX_seat_categories_showtime_id_normalized_name");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_seat_categories_showtime_id_id",
                table: "seat_categories",
                columns: new[] { "showtime_id", "id" });

            migrationBuilder.AddForeignKey(
                name: "FK_seat_categories_showtimes_showtime_id",
                table: "seat_categories",
                column: "showtime_id",
                principalTable: "showtimes",
                principalColumn: "id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_seats_seat_categories_showtime_id_seat_category_id",
                table: "seats",
                columns: new[] { "showtime_id", "seat_category_id" },
                principalTable: "seat_categories",
                principalColumns: new[] { "showtime_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_seat_categories_showtimes_showtime_id",
                table: "seat_categories");

            migrationBuilder.DropForeignKey(
                name: "FK_seats_seat_categories_showtime_id_seat_category_id",
                table: "seats");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_seat_categories_showtime_id_id",
                table: "seat_categories");

            migrationBuilder.RenameColumn(
                name: "showtime_id",
                table: "seats",
                newName: "performance_id");

            migrationBuilder.RenameIndex(
                name: "IX_seats_showtime_id_status_held_until",
                table: "seats",
                newName: "IX_seats_performance_id_status_held_until");

            migrationBuilder.RenameIndex(
                name: "IX_seats_showtime_id_seat_category_id",
                table: "seats",
                newName: "IX_seats_performance_id_seat_category_id");

            migrationBuilder.RenameIndex(
                name: "IX_seats_showtime_id_row_number",
                table: "seats",
                newName: "IX_seats_performance_id_row_number");

            migrationBuilder.RenameColumn(
                name: "showtime_id",
                table: "seat_categories",
                newName: "performance_id");

            migrationBuilder.RenameIndex(
                name: "IX_seat_categories_showtime_id_normalized_name",
                table: "seat_categories",
                newName: "IX_seat_categories_performance_id_normalized_name");

            migrationBuilder.AddUniqueConstraint(
                name: "AK_seat_categories_performance_id_id",
                table: "seat_categories",
                columns: new[] { "performance_id", "id" });

            migrationBuilder.CreateTable(
                name: "performances",
                columns: table => new
                {
                    id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    end_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    is_sales_closed = table.Column<bool>(type: "boolean", nullable: false),
                    start_time = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    title = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_performances", x => x.id);
                });

            migrationBuilder.AddForeignKey(
                name: "FK_seats_seat_categories_performance_id_seat_category_id",
                table: "seats",
                columns: new[] { "performance_id", "seat_category_id" },
                principalTable: "seat_categories",
                principalColumns: new[] { "performance_id", "id" },
                onDelete: ReferentialAction.Restrict);
        }
    }
}
