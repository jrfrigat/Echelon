-- Creates the archive database beside the operational one.
--
-- POSTGRES_DB creates exactly one database, and this deployment needs two: the operational store
-- and the archive the runner moves rows into after 90 days. They are separate so that operational
-- queries stay off two years of history and the archive can be backed up on its own schedule.
--
-- Run by the postgres image only when the data directory is empty. Adding it to a volume that
-- already has data does nothing - create the database by hand there.
--
-- The SQL Server side has no equivalent because its image creates no database at all: EF's
-- migrations create both. PostgreSQL creates POSTGRES_DB itself, so only the second one is left.
SELECT 'CREATE DATABASE releaseorchestrator_archive'
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = 'releaseorchestrator_archive')\gexec
