-- PRP-002: the apply time of a batch, and an index for reading one employee's values including superseded ones.
ALTER TABLE ingestion_batches ADD COLUMN applied_at timestamptz;

-- Backfill. This runs as the table owner, and FORCE ROW LEVEL SECURITY hides every row from the owner while no
-- tenant is set, so it is lifted for the backfill and restored below, in this same transaction. Both statements
-- take an ACCESS EXCLUSIVE lock, so no other session sees the tables without FORCE.
ALTER TABLE ingestion_batches NO FORCE ROW LEVEL SECURITY;
ALTER TABLE compensation_values NO FORCE ROW LEVEL SECURITY;

-- The apply time of an Applied batch is the recorded_at of a value it inserted; else the superseded_at of a value
-- it superseded; else, when every row was unchanged, completed_at: that batch changed no value, so any instant
-- during it gives the same answers.
-- Each side is aggregated once (one pass over the values), not looked up per batch.
UPDATE ingestion_batches b
SET applied_at = coalesce(inserted.recorded_at, superseded.superseded_at, b.completed_at)
FROM ingestion_batches batch
LEFT JOIN (
  SELECT batch_id, min(recorded_at) AS recorded_at
  FROM compensation_values
  GROUP BY batch_id
) inserted ON inserted.batch_id = batch.id
LEFT JOIN (
  SELECT superseded_by_batch_id AS batch_id, min(superseded_at) AS superseded_at
  FROM compensation_values
  WHERE superseded_by_batch_id IS NOT NULL
  GROUP BY superseded_by_batch_id
) superseded ON superseded.batch_id = batch.id
WHERE batch.id = b.id AND b.status = 'Applied';

ALTER TABLE ingestion_batches FORCE ROW LEVEL SECURITY;
ALTER TABLE compensation_values FORCE ROW LEVEL SECURITY;

ALTER TABLE ingestion_batches
  ADD CONSTRAINT ck_ingestion_batches_applied_at CHECK ((status = 'Applied') = (applied_at IS NOT NULL));

-- The PRP-001 unique index covers current rows only; as-known-at reads need superseded rows too.
CREATE INDEX ix_compensation_values_employee
  ON compensation_values (tenant_id, employee_id, component, effective_from);
