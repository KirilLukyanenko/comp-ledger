CREATE TABLE ingestion_batches (
  id              uuid PRIMARY KEY,
  tenant_id       uuid NOT NULL REFERENCES tenants(id),
  status          text NOT NULL CHECK (status IN ('Applied', 'Rejected')),
  received_at     timestamptz NOT NULL,
  completed_at    timestamptz NOT NULL,
  total_rows      int NOT NULL,
  inserted_rows   int NOT NULL,
  corrected_rows  int NOT NULL,
  unchanged_rows  int NOT NULL,
  error_count     int NOT NULL,
  errors          jsonb NOT NULL DEFAULT '[]'   -- at most 100 entries
);
