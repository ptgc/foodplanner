using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Foodplanner.Infrastructure.Persistence.Migrations
{
    /// <summary>
    /// T-074 (Q-016): moves the registration time from the Identity claim <c>foodplanner:registered-at</c> (T-072) to
    /// <c>AspNetUsers.RegisteredAt</c>. Unconfirmed users without a claim (from before T-072) stay null; the cleanup
    /// starts their window on its first pass, as before.
    /// </summary>
    public partial class AddUserRegisteredAt : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RegisteredAt",
                table: "AspNetUsers",
                type: "datetimeoffset",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUsers_RegisteredAt_Unconfirmed",
                table: "AspNetUsers",
                column: "RegisteredAt",
                filter: "[EmailConfirmed] = 0");

            // Backfill: the newest claim per unconfirmed user (zero-padded unix seconds). Day and second parts are
            // added separately so values past 2038 do not overflow DATEADD's int argument.
            migrationBuilder.Sql("""
                UPDATE u
                SET RegisteredAt = DATEADD(SECOND, s.Secs % 86400, DATEADD(DAY, s.Secs / 86400, CAST('1970-01-01T00:00:00+00:00' AS datetimeoffset)))
                FROM AspNetUsers u
                JOIN (
                    SELECT UserId, MAX(TRY_CONVERT(bigint, ClaimValue)) AS Secs
                    FROM AspNetUserClaims
                    WHERE ClaimType = N'foodplanner:registered-at'
                    GROUP BY UserId
                ) s ON s.UserId = u.Id
                WHERE u.EmailConfirmed = 0 AND s.Secs IS NOT NULL
                """);

            // The claims are no longer read: remove them all, including those left on confirmed users.
            migrationBuilder.Sql("DELETE FROM AspNetUserClaims WHERE ClaimType = N'foodplanner:registered-at'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Restore one claim per unconfirmed user that has a time. Nothing is restored for confirmed users (the
            // column is null for them, and a claim on them was ignored anyway).
            migrationBuilder.Sql("""
                INSERT INTO AspNetUserClaims (UserId, ClaimType, ClaimValue)
                SELECT Id, N'foodplanner:registered-at',
                    RIGHT('000000000000' + CAST(DATEDIFF_BIG(SECOND, CAST('1970-01-01T00:00:00+00:00' AS datetimeoffset), RegisteredAt) AS varchar(20)), 12)
                FROM AspNetUsers
                WHERE EmailConfirmed = 0 AND RegisteredAt IS NOT NULL
                """);

            migrationBuilder.DropIndex(
                name: "IX_AspNetUsers_RegisteredAt_Unconfirmed",
                table: "AspNetUsers");

            migrationBuilder.DropColumn(
                name: "RegisteredAt",
                table: "AspNetUsers");
        }
    }
}
