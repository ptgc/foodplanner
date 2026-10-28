using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

#pragma warning disable CA1814 // Prefer jagged arrays over multidimensional

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMargarineAndOils : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "CatalogTranslations",
                columns: new[] { "EntityId", "Kind", "Locale", "Name" },
                values: new object[,]
                {
                    { new Guid("696b64ff-38fe-5660-b130-925b51a2e1f1"), "ingredient", "en", "Oil" },
                    { new Guid("696b64ff-38fe-5660-b130-925b51a2e1f1"), "ingredient", "nb", "Olje" },
                    { new Guid("bf538d98-0152-59f1-b4f2-7385fe43711a"), "ingredient", "en", "Neutral oil" },
                    { new Guid("bf538d98-0152-59f1-b4f2-7385fe43711a"), "ingredient", "nb", "Nøytral olje" },
                    { new Guid("d2919ba2-38a0-5991-abb5-06a3a01ece4b"), "ingredient", "en", "Margarine" },
                    { new Guid("d2919ba2-38a0-5991-abb5-06a3a01ece4b"), "ingredient", "nb", "Margarin" }
                });

            migrationBuilder.InsertData(
                table: "Ingredients",
                columns: new[] { "Id", "CreatedAt", "Dimension", "HouseholdId", "IsStaple", "Name", "UpdatedAt" },
                values: new object[,]
                {
                    { new Guid("696b64ff-38fe-5660-b130-925b51a2e1f1"), new DateTimeOffset(new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Volume", null, true, "Oil", new DateTimeOffset(new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("bf538d98-0152-59f1-b4f2-7385fe43711a"), new DateTimeOffset(new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Volume", null, true, "Neutral oil", new DateTimeOffset(new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) },
                    { new Guid("d2919ba2-38a0-5991-abb5-06a3a01ece4b"), new DateTimeOffset(new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)), "Mass", null, true, "Margarine", new DateTimeOffset(new DateTime(2026, 10, 24, 0, 0, 0, 0, DateTimeKind.Unspecified), new TimeSpan(0, 0, 0, 0, 0)) }
                });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "CatalogTranslations",
                keyColumns: new[] { "EntityId", "Kind", "Locale" },
                keyValues: new object[] { new Guid("696b64ff-38fe-5660-b130-925b51a2e1f1"), "ingredient", "en" });

            migrationBuilder.DeleteData(
                table: "CatalogTranslations",
                keyColumns: new[] { "EntityId", "Kind", "Locale" },
                keyValues: new object[] { new Guid("696b64ff-38fe-5660-b130-925b51a2e1f1"), "ingredient", "nb" });

            migrationBuilder.DeleteData(
                table: "CatalogTranslations",
                keyColumns: new[] { "EntityId", "Kind", "Locale" },
                keyValues: new object[] { new Guid("bf538d98-0152-59f1-b4f2-7385fe43711a"), "ingredient", "en" });

            migrationBuilder.DeleteData(
                table: "CatalogTranslations",
                keyColumns: new[] { "EntityId", "Kind", "Locale" },
                keyValues: new object[] { new Guid("bf538d98-0152-59f1-b4f2-7385fe43711a"), "ingredient", "nb" });

            migrationBuilder.DeleteData(
                table: "CatalogTranslations",
                keyColumns: new[] { "EntityId", "Kind", "Locale" },
                keyValues: new object[] { new Guid("d2919ba2-38a0-5991-abb5-06a3a01ece4b"), "ingredient", "en" });

            migrationBuilder.DeleteData(
                table: "CatalogTranslations",
                keyColumns: new[] { "EntityId", "Kind", "Locale" },
                keyValues: new object[] { new Guid("d2919ba2-38a0-5991-abb5-06a3a01ece4b"), "ingredient", "nb" });

            migrationBuilder.DeleteData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("696b64ff-38fe-5660-b130-925b51a2e1f1"));

            migrationBuilder.DeleteData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("bf538d98-0152-59f1-b4f2-7385fe43711a"));

            migrationBuilder.DeleteData(
                table: "Ingredients",
                keyColumn: "Id",
                keyValue: new Guid("d2919ba2-38a0-5991-abb5-06a3a01ece4b"));
        }
    }
}
