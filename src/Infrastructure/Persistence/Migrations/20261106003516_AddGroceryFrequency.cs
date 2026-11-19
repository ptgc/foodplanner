using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGroceryFrequency : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Frequency",
                table: "WeeklyGroceries",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Weekly");

            migrationBuilder.AddColumn<DateOnly>(
                name: "StartWeek",
                table: "WeeklyGroceries",
                type: "date",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Frequency",
                table: "WeeklyGroceries");

            migrationBuilder.DropColumn(
                name: "StartWeek",
                table: "WeeklyGroceries");
        }
    }
}
