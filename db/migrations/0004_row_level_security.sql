-- Role provisioning is the environment's job (PRP-001 6.4). Fail early and clearly if it was not done.
DO $$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'comp_app') THEN
    RAISE EXCEPTION 'Role "comp_app" does not exist. Create it (NOLOGIN) and a LOGIN role that is a member of it before running the migrations.';
  END IF;
END
$$;

-- The policies fail closed: with no tenant set, the setting is NULL or '', the comparison is NULL, and no row matches.
ALTER TABLE ingestion_batches ENABLE ROW LEVEL SECURITY;
ALTER TABLE ingestion_batches FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON ingestion_batches
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

ALTER TABLE compensation_values ENABLE ROW LEVEL SECURITY;
ALTER TABLE compensation_values FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON compensation_values
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

-- tenants has no policy: the app role may only read and create tenants.
GRANT SELECT, INSERT ON tenants TO comp_app;
GRANT SELECT, INSERT, UPDATE ON ingestion_batches TO comp_app;
GRANT SELECT, INSERT, UPDATE ON compensation_values TO comp_app;
