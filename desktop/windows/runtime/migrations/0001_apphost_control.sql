BEGIN;

SELECT pg_advisory_xact_lock(7626654561034270001);

CREATE SCHEMA IF NOT EXISTS desktop_runtime;

CREATE TABLE IF NOT EXISTS desktop_runtime.apphost_control
(
    singleton_key boolean NOT NULL DEFAULT true,
    cluster_id uuid NOT NULL,
    schema_version integer NOT NULL,
    CONSTRAINT apphost_control_pkey PRIMARY KEY (singleton_key),
    CONSTRAINT apphost_control_singleton_true CHECK (singleton_key),
    CONSTRAINT apphost_control_schema_nonnegative CHECK (schema_version >= 0)
);

DO $shape$
DECLARE
    control_table oid := 'desktop_runtime.apphost_control'::regclass;
BEGIN
    IF (SELECT count(*)
          FROM pg_catalog.pg_attribute AS a
         WHERE a.attrelid = control_table
           AND a.attnum > 0
           AND NOT a.attisdropped) <> 3
       OR NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_attribute AS a
             WHERE a.attrelid = control_table AND a.attname = 'singleton_key'
               AND a.atttypid = 'boolean'::regtype AND a.attnotnull)
       OR NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_attribute AS a
             WHERE a.attrelid = control_table AND a.attname = 'cluster_id'
               AND a.atttypid = 'uuid'::regtype AND a.attnotnull)
       OR NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_attribute AS a
             WHERE a.attrelid = control_table AND a.attname = 'schema_version'
               AND a.atttypid = 'integer'::regtype AND a.attnotnull)
       OR NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_constraint AS c
             WHERE c.conrelid = control_table
               AND c.conname = 'apphost_control_pkey'
               AND c.contype = 'p'
               AND c.convalidated
               AND pg_catalog.pg_get_constraintdef(c.oid, false) = 'PRIMARY KEY (singleton_key)')
       OR NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_constraint AS c
             WHERE c.conrelid = control_table
               AND c.conname = 'apphost_control_singleton_true'
               AND c.contype = 'c'
               AND c.convalidated
               AND pg_catalog.pg_get_constraintdef(c.oid, false) = 'CHECK (singleton_key)')
       OR NOT EXISTS (
            SELECT 1 FROM pg_catalog.pg_constraint AS c
             WHERE c.conrelid = control_table
               AND c.conname = 'apphost_control_schema_nonnegative'
               AND c.contype = 'c'
               AND c.convalidated
               AND pg_catalog.pg_get_constraintdef(c.oid, false) = 'CHECK ((schema_version >= 0))')
    THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'apphost-control-shape-invalid';
    END IF;
END
$shape$;

CREATE TEMPORARY TABLE apphost_migration_parameters ON COMMIT DROP AS
SELECT
    :'expected_cluster_id'::uuid AS expected_cluster_id,
    :'starting_schema_version'::integer AS starting_schema_version,
    :'target_schema_version'::integer AS target_schema_version;

INSERT INTO desktop_runtime.apphost_control (singleton_key, cluster_id, schema_version)
SELECT true, expected_cluster_id, starting_schema_version
FROM apphost_migration_parameters
ON CONFLICT (singleton_key) DO NOTHING;

DO $migration$
DECLARE
    actual_count bigint;
    actual_cluster_id uuid;
    actual_schema_version integer;
    expected_cluster_id uuid;
    starting_schema_version integer;
    target_schema_version integer;
BEGIN
    SELECT p.expected_cluster_id, p.starting_schema_version, p.target_schema_version
      INTO expected_cluster_id, starting_schema_version, target_schema_version
      FROM apphost_migration_parameters AS p;

    SELECT count(*)
      INTO actual_count
      FROM desktop_runtime.apphost_control AS c;

    IF actual_count <> 1 THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'apphost-control-row-count-invalid';
    END IF;

    SELECT c.cluster_id, c.schema_version
      INTO actual_cluster_id, actual_schema_version
      FROM desktop_runtime.apphost_control AS c
     WHERE c.singleton_key;
    IF actual_cluster_id <> expected_cluster_id THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'apphost-cluster-id-mismatch';
    END IF;

    IF actual_schema_version = starting_schema_version THEN
        UPDATE desktop_runtime.apphost_control
           SET schema_version = target_schema_version
         WHERE singleton_key AND schema_version = starting_schema_version;
    ELSIF actual_schema_version <> target_schema_version THEN
        RAISE EXCEPTION USING ERRCODE = 'P0001', MESSAGE = 'apphost-schema-transition-invalid';
    END IF;
END
$migration$;

COMMIT;
