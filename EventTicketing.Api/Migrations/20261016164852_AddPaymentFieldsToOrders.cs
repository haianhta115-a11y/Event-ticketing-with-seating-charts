using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace EventTicketing.Api.Migrations
{
    /// <inheritdoc />
    public partial class AddPaymentFieldsToOrders : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "paid_amount",
                table: "orders",
                type: "numeric(18,2)",
                precision: 18,
                scale: 2,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "paid_at",
                table: "orders",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_reference",
                table: "orders",
                type: "character varying(255)",
                maxLength: 255,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "payment_review_reason",
                table: "orders",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "requires_payment_review",
                table: "orders",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "paid_amount",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "paid_at",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_reference",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "payment_review_reason",
                table: "orders");

            migrationBuilder.DropColumn(
                name: "requires_payment_review",
                table: "orders");
        }
    }
}
