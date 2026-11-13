-- PRP-002: a snapshot is a named pair (as_of, known_at) of a tenant. It stores no compensation values.
CREATE TABLE snapshots (
  id        uuid PRIMARY KEY,
  tenant_id uuid NOT NULL REFERENCES tenants(id),
  name      text NOT NULL,
  as_of     date NOT NULL,
  known_at  timestamptz NOT NULL
);

-- Names are unique within a tenant. The index decides it, not a check before the insert.
CREATE UNIQUE INDEX ux_snapshots_tenant_name ON snapshots (tenant_id, name);

-- Tenant-owned, like the PRP-001 tables: the policy fails closed when no tenant is set.
ALTER TABLE snapshots ENABLE ROW LEVEL SECURITY;
ALTER TABLE snapshots FORCE ROW LEVEL SECURITY;
CREATE POLICY tenant_isolation ON snapshots
  USING (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid)
  WITH CHECK (tenant_id = nullif(current_setting('app.tenant_id', true), '')::uuid);

-- Snapshots are immutable: the app role may create and read them, nothing else.
GRANT SELECT, INSERT ON snapshots TO comp_app;
