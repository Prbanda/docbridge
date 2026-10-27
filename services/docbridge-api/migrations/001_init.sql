-- docbridge database (TDD Database Design + IMPLEMENTATION_PLAN amendments AM-1…AM-6, plan gaps G5/G8).
-- team/binder/folder/user ids belong to other services: plain uuids, validated through service APIs.

CREATE TABLE jobs (
  id                   uuid PRIMARY KEY,
  submitted_by_user_id uuid NOT NULL,
  task_count           int NOT NULL,
  idempotency_key      text,                 -- AM-5
  enqueued_at          timestamptz,          -- G8: set by fan-out once every task message is sent
  completed_at         timestamptz,          -- set when the last task resolves
  created_at           timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_jobs_user ON jobs(submitted_by_user_id, created_at DESC);
CREATE UNIQUE INDEX uq_jobs_idempotency ON jobs(submitted_by_user_id, idempotency_key) WHERE idempotency_key IS NOT NULL;
CREATE INDEX idx_jobs_not_enqueued ON jobs(created_at) WHERE enqueued_at IS NULL;

CREATE TABLE files (
  id              uuid PRIMARY KEY,
  owner_user_id   uuid NOT NULL,
  job_id          uuid REFERENCES jobs(id),  -- NULL until submitted (claimed atomically, AM-1)
  original_name   text NOT NULL,
  size_bytes      bigint NOT NULL,
  content_type    text NOT NULL,
  checksum_sha256 text NOT NULL,             -- hex; client-declared, verified by S3 + worker + platform
  s3_key          text NOT NULL,             -- staging/{owner_user_id}/{file_id}
  created_at      timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_files_job ON files(job_id);
CREATE INDEX idx_files_unsubmitted ON files(created_at) WHERE job_id IS NULL;  -- AM-3 sweeper

CREATE TABLE tasks (
  id                   uuid PRIMARY KEY,
  job_id               uuid NOT NULL REFERENCES jobs(id),
  file_id              uuid NOT NULL REFERENCES files(id),
  team_id              uuid NOT NULL,
  binder_id            uuid NOT NULL,
  folder_id            uuid,                 -- NULL = binder root
  region               text NOT NULL,
  destination_label    text,                 -- G5: display name supplied at submission
  status               text NOT NULL DEFAULT 'pending'
                       CHECK (status IN ('pending','in_progress','completed','failed')),
  attempt_count        int NOT NULL DEFAULT 0,
  failure_reason       text,
  platform_document_id uuid,
  created_at           timestamptz NOT NULL DEFAULT now(),
  updated_at           timestamptz NOT NULL DEFAULT now()
);
-- Duplicate-destination guard. Postgres >= 15 (A4): one constraint covers binder-root (NULL folder) destinations.
CREATE UNIQUE INDEX uq_tasks_dest ON tasks(file_id, team_id, binder_id, folder_id) NULLS NOT DISTINCT;
CREATE INDEX idx_tasks_job_status ON tasks(job_id, status);
CREATE INDEX idx_tasks_file ON tasks(file_id);  -- AM-6

CREATE TABLE ws_connections (
  connection_id text PRIMARY KEY,
  user_id       uuid NOT NULL,
  job_id        uuid,
  connected_at  timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_ws_job ON ws_connections(job_id);
CREATE INDEX idx_ws_connected ON ws_connections(connected_at);  -- AM-4 sweeper
