-- PRP-001 2.0 (6.4): no foreign keys on compensation_values. PostgreSQL checks a foreign key once per inserted or
-- updated row, and those checks took most of the time of a 1M-row batch. The pipeline guarantees the references:
-- the tenant is resolved from tenants before the upload, and the batch row is inserted in the same transaction
-- before any value. tenants and ingestion_batches keep their own keys.
ALTER TABLE compensation_values
  DROP CONSTRAINT compensation_values_tenant_id_fkey,
  DROP CONSTRAINT compensation_values_batch_id_fkey,
  DROP CONSTRAINT compensation_values_superseded_by_batch_id_fkey;
