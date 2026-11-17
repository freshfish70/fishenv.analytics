using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Postgres.Migrations
{
    /// <summary>
    /// Daily salts are now stored sealed with AES-GCM under Ingest:SaltKey (security review 2026-11-09, finding 1):
    /// a plaintext salt next to the hashes let anyone with a dump or backup recover that day's visitor IPs. This
    /// deletes the plaintext salts stored so far; ingest stores a sealed one with the next event. Visitors seen
    /// earlier on the day of the upgrade are counted again. No schema change.
    /// </summary>
    public partial class SealDailySalts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DELETE FROM daily_salts;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Older ingest versions would use a sealed value as a plain salt, so drop those too.
            migrationBuilder.Sql("DELETE FROM daily_salts;");
        }
    }
}
