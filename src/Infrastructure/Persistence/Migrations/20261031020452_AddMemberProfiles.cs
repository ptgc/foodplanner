using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMemberProfiles : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedAt",
                table: "MealPlans",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedByUserId",
                table: "MealPlans",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PickedByProfileId",
                table: "MealPlanEntries",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MemberProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    DisplayName = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Avatar = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MemberProfiles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_MemberProfiles_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "UserPins",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Hash = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    FailedCount = table.Column<int>(type: "int", nullable: false),
                    LockedUntil = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: true),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserPins", x => x.UserId);
                    table.ForeignKey(
                        name: "FK_UserPins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "RecipeLikes",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    HouseholdId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    MemberProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "datetimeoffset", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeLikes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeLikes_Households_HouseholdId",
                        column: x => x.HouseholdId,
                        principalTable: "Households",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_RecipeLikes_MemberProfiles_MemberProfileId",
                        column: x => x.MemberProfileId,
                        principalTable: "MemberProfiles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeLikes_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_MealPlans_LockedByUserId",
                table: "MealPlans",
                column: "LockedByUserId");

            migrationBuilder.CreateIndex(
                name: "IX_MealPlanEntries_PickedByProfileId",
                table: "MealPlanEntries",
                column: "PickedByProfileId");

            migrationBuilder.CreateIndex(
                name: "IX_MemberProfiles_HouseholdId_DisplayName",
                table: "MemberProfiles",
                columns: new[] { "HouseholdId", "DisplayName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeLikes_HouseholdId",
                table: "RecipeLikes",
                column: "HouseholdId");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeLikes_MemberProfileId_RecipeId",
                table: "RecipeLikes",
                columns: new[] { "MemberProfileId", "RecipeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeLikes_RecipeId",
                table: "RecipeLikes",
                column: "RecipeId");

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlanEntries_MemberProfiles_PickedByProfileId",
                table: "MealPlanEntries",
                column: "PickedByProfileId",
                principalTable: "MemberProfiles",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_MealPlans_AspNetUsers_LockedByUserId",
                table: "MealPlans",
                column: "LockedByUserId",
                principalTable: "AspNetUsers",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_MealPlanEntries_MemberProfiles_PickedByProfileId",
                table: "MealPlanEntries");

            migrationBuilder.DropForeignKey(
                name: "FK_MealPlans_AspNetUsers_LockedByUserId",
                table: "MealPlans");

            migrationBuilder.DropTable(
                name: "RecipeLikes");

            migrationBuilder.DropTable(
                name: "UserPins");

            migrationBuilder.DropTable(
                name: "MemberProfiles");

            migrationBuilder.DropIndex(
                name: "IX_MealPlans_LockedByUserId",
                table: "MealPlans");

            migrationBuilder.DropIndex(
                name: "IX_MealPlanEntries_PickedByProfileId",
                table: "MealPlanEntries");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                table: "MealPlans");

            migrationBuilder.DropColumn(
                name: "LockedByUserId",
                table: "MealPlans");

            migrationBuilder.DropColumn(
                name: "PickedByProfileId",
                table: "MealPlanEntries");
        }
    }
}
