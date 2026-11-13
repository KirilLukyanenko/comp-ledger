CREATE TABLE compensation_values (
  id                      bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
  tenant_id               uuid NOT NULL REFERENCES tenants(id),
  employee_id             text NOT NULL,
  component               text NOT NULL,
  effective_from          date NOT NULL,
  amount                  numeric(19,4) NOT NULL,
  currency                char(3),
  batch_id                uuid NOT NULL REFERENCES ingestion_batches(id),
  recorded_at             timestamptz NOT NULL,
  superseded_at           timestamptz,
  superseded_by_batch_id  uuid REFERENCES ingestion_batches(id)
);

-- one current value per key
CREATE UNIQUE INDEX ux_compensation_current
  ON compensation_values (tenant_id, employee_id, component, effective_from)
  WHERE superseded_at IS NULL;
