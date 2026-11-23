-- Runs once when the PostgreSQL volume is created.
-- The role and database come from POSTGRES_USER / POSTGRES_DB of the official image;
-- here we only add the extensions BlueMail needs (multilingual search, uuid).
\connect bluemail
CREATE EXTENSION IF NOT EXISTS unaccent;
CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS pgcrypto;
