-- idx_tasks_file (AM-6) is fully covered by uq_tasks_dest, whose leading column is file_id, so it only added
-- index maintenance to every 2,000-row submission insert.
DROP INDEX IF EXISTS idx_tasks_file;
