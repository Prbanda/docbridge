-- auth database (TDD Database Design). citext is a trusted extension: the database owner may create it.
CREATE EXTENSION IF NOT EXISTS citext;

CREATE TABLE users (
  id            uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  email         citext UNIQUE NOT NULL,
  password_hash text NOT NULL,
  name          text NOT NULL,
  created_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE service_clients (
  id                 uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  client_id          text UNIQUE NOT NULL,
  client_secret_hash text NOT NULL,
  scopes             text[] NOT NULL,
  created_at         timestamptz NOT NULL DEFAULT now()
);
