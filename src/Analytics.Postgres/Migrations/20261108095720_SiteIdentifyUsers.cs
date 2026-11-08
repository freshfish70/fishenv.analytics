using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Postgres.Migrations
{
    /// <inheritdoc />
    public partial class SiteIdentifyUsers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "identify_users",
                table: "sites",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "identify_users",
                table: "sites");
        }
    }
}
