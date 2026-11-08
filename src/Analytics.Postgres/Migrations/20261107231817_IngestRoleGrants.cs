using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Analytics.Postgres.Migrations
{
    /// <summary>
    /// Grants the ingest role exactly what the public ingest service needs (see db/grants.sql for the rationale).
    /// analytics_admin owns the database and the tables, so it can grant these itself; no superuser step is needed.
    /// Skipped with a notice if the role does not exist (a custom role name: apply db/grants.sql by hand).
    /// On PostgreSQL 14 the schema grant is a no-op warning, because PUBLIC already has USAGE on "public" there.
    /// </summary>
    public partial class IngestRoleGrants : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $grants$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'analytics_ingest') THEN
                        EXECUTE format('GRANT CONNECT ON DATABASE %I TO analytics_ingest', current_database());
                        GRANT USAGE ON SCHEMA public TO analytics_ingest;
                        GRANT INSERT ON events TO analytics_ingest;
                        GRANT SELECT ON sites TO analytics_ingest;
                        GRANT SELECT, INSERT, DELETE ON daily_salts TO analytics_ingest;
                    ELSE
                        RAISE NOTICE 'role analytics_ingest does not exist; apply db/grants.sql for your ingest role';
                    END IF;
                END
                $grants$;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $grants$
                BEGIN
                    IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'analytics_ingest') THEN
                        REVOKE ALL ON events, sites, daily_salts FROM analytics_ingest;
                    END IF;
                END
                $grants$;
                """);
        }
    }
}
