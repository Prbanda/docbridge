-- platform database (TDD Database Design). user ids come from the auth service: no cross-database FK.
CREATE TABLE teams (
  id                uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  name              text NOT NULL,
  region            text NOT NULL,
  docbridge_enabled boolean NOT NULL DEFAULT true,
  created_at        timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE team_members (
  team_id  uuid REFERENCES teams(id),
  user_id  uuid NOT NULL,
  added_at timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (team_id, user_id)
);
CREATE INDEX idx_team_members_user ON team_members(user_id);

CREATE TABLE binders (
  id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  team_id    uuid NOT NULL REFERENCES teams(id),
  name       text NOT NULL,
  created_at timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_binders_team ON binders(team_id);

CREATE TABLE folders (
  id               uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  binder_id        uuid NOT NULL REFERENCES binders(id),
  parent_folder_id uuid REFERENCES folders(id),
  name             text NOT NULL,
  created_at       timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_folders_parent ON folders(binder_id, parent_folder_id);

CREATE TABLE documents (
  id                  uuid PRIMARY KEY DEFAULT gen_random_uuid(),
  binder_id           uuid NOT NULL REFERENCES binders(id),
  folder_id           uuid REFERENCES folders(id),
  name                text NOT NULL,
  size_bytes          bigint NOT NULL,
  content_type        text NOT NULL,
  checksum_sha256     text NOT NULL,
  source_task_id      uuid UNIQUE,
  uploaded_by_user_id uuid NOT NULL,
  storage_key         text NOT NULL,
  created_at          timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX idx_documents_location ON documents(binder_id, folder_id);
