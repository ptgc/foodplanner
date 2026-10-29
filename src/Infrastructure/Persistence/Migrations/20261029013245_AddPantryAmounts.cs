using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPantryAmounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<decimal>(
                name: "AtHomeAmount",
                table: "ShoppingListItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "AtHomeUnitId",
                table: "ShoppingListItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsAtHome",
                table: "ShoppingListItems",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<decimal>(
                name: "Amount",
                table: "PantryItems",
                type: "decimal(18,4)",
                precision: 18,
                scale: 4,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "UnitId",
                table: "PantryItems",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_ShoppingListItems_AtHomeUnitId",
                table: "ShoppingListItems",
                column: "AtHomeUnitId");

            migrationBuilder.CreateIndex(
                name: "IX_PantryItems_UnitId",
                table: "PantryItems",
                column: "UnitId");

            migrationBuilder.AddForeignKey(
                name: "FK_PantryItems_Units_UnitId",
                table: "PantryItems",
                column: "UnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_ShoppingListItems_Units_AtHomeUnitId",
                table: "ShoppingListItems",
                column: "AtHomeUnitId",
                principalTable: "Units",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_PantryItems_Units_UnitId",
                table: "PantryItems");

            migrationBuilder.DropForeignKey(
                name: "FK_ShoppingListItems_Units_AtHomeUnitId",
                table: "ShoppingListItems");

            migrationBuilder.DropIndex(
                name: "IX_ShoppingListItems_AtHomeUnitId",
                table: "ShoppingListItems");

            migrationBuilder.DropIndex(
                name: "IX_PantryItems_UnitId",
                table: "PantryItems");

            migrationBuilder.DropColumn(
                name: "AtHomeAmount",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "AtHomeUnitId",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "IsAtHome",
                table: "ShoppingListItems");

            migrationBuilder.DropColumn(
                name: "Amount",
                table: "PantryItems");

            migrationBuilder.DropColumn(
                name: "UnitId",
                table: "PantryItems");
        }
    }
}
