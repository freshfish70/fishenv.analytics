-- The API service applies these grants itself (migration IngestRoleGrants), because analytics_admin owns the
-- database and the tables. Run this file by hand only if
--   * your ingest role is not called analytics_ingest (replace the name below), or
--   * you are on PostgreSQL 14 or older, where PUBLIC may create objects in schema "public" (the REVOKE needs the
--     schema owner, usually a superuser):
--       sudo -u postgres psql -d analytics -f db/grants.sql      (after the API has started once)
--
-- The public ingest service can only add events, read the site list and manage the daily salts. If it is ever
-- compromised it cannot read analytics data, API keys or change sites.
REVOKE CREATE ON SCHEMA public FROM PUBLIC;

GRANT CONNECT ON DATABASE analytics TO analytics_ingest;
GRANT USAGE   ON SCHEMA public      TO analytics_ingest;

GRANT INSERT                 ON events      TO analytics_ingest;
GRANT SELECT                 ON sites       TO analytics_ingest;
GRANT SELECT, INSERT, DELETE ON daily_salts TO analytics_ingest;
