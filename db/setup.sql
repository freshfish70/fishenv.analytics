-- Run once as a Postgres superuser (e.g. `sudo -u postgres psql -f db/setup.sql`) BEFORE the first start.
-- Replace both passwords. analytics_admin owns the schema and is used by the API service (it applies migrations);
-- analytics_ingest is the restricted role used by the public ingest service (see grants.sql).
CREATE ROLE analytics_admin  LOGIN PASSWORD 'change-me-admin';
CREATE ROLE analytics_ingest LOGIN PASSWORD 'change-me-ingest';
CREATE DATABASE analytics OWNER analytics_admin;
