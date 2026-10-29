#!/bin/bash
# Creates the 3 isolated logical databases + per-service roles (ADR-009).
# Runs once on first container start via /docker-entrypoint-initdb.d.
# Local-dev passwords come from compose env; real environments use Secrets Manager.
set -euo pipefail

psql -v ON_ERROR_STOP=1 --username "$POSTGRES_USER" <<-SQL
  CREATE ROLE auth_svc      LOGIN PASSWORD '${AUTH_DB_PASSWORD}';
  CREATE ROLE platform_svc  LOGIN PASSWORD '${PLATFORM_DB_PASSWORD}';
  CREATE ROLE docbridge_svc LOGIN PASSWORD '${DOCBRIDGE_DB_PASSWORD}';

  CREATE DATABASE auth      OWNER auth_svc;
  CREATE DATABASE platform  OWNER platform_svc;
  CREATE DATABASE docbridge OWNER docbridge_svc;

  -- Ownership enforced by construction: only the owning role may connect (ADR-009).
  REVOKE CONNECT ON DATABASE auth      FROM PUBLIC;
  REVOKE CONNECT ON DATABASE platform  FROM PUBLIC;
  REVOKE CONNECT ON DATABASE docbridge FROM PUBLIC;
  GRANT  CONNECT ON DATABASE auth      TO auth_svc;
  GRANT  CONNECT ON DATABASE platform  TO platform_svc;
  GRANT  CONNECT ON DATABASE docbridge TO docbridge_svc;
SQL

echo "docbridge: created 3 logical databases with per-service roles"
