SELECT set_config('barkcloud.owner_role', :'app_role', false);
SELECT set_config('barkcloud.owner_schemas', :'app_schemas', false);

DO $$
DECLARE
    owner_role name := current_setting('barkcloud.owner_role');
    schema_names text[] := string_to_array(current_setting('barkcloud.owner_schemas'), ',');
    item record;
    statement text;
BEGIN
    FOR item IN
        SELECT nspname
        FROM pg_namespace
        WHERE nspname = ANY(schema_names)
          AND nspname NOT LIKE 'pg_%'
          AND nspname <> 'information_schema'
    LOOP
        EXECUTE format('ALTER SCHEMA %I OWNER TO %I', item.nspname, owner_role);
    END LOOP;

    FOR item IN
        SELECT namespace.nspname, relation.relname, relation.relkind
        FROM pg_class relation
        JOIN pg_namespace namespace ON namespace.oid = relation.relnamespace
        WHERE namespace.nspname = ANY(schema_names)
          AND relation.relkind IN ('r', 'p', 'v', 'm', 'f', 'S')
          AND NOT EXISTS (
              SELECT 1 FROM pg_depend dependency
              WHERE dependency.classid = 'pg_class'::regclass
                AND dependency.objid = relation.oid
                AND dependency.deptype = 'e')
        -- Table ownership also updates its owned sequences; process standalone sequences afterwards.
        ORDER BY CASE WHEN relation.relkind = 'S' THEN 1 ELSE 0 END
    LOOP
        statement := CASE item.relkind
            WHEN 'S' THEN 'ALTER SEQUENCE %I.%I OWNER TO %I'
            WHEN 'v' THEN 'ALTER VIEW %I.%I OWNER TO %I'
            WHEN 'm' THEN 'ALTER MATERIALIZED VIEW %I.%I OWNER TO %I'
            WHEN 'f' THEN 'ALTER FOREIGN TABLE %I.%I OWNER TO %I'
            ELSE 'ALTER TABLE %I.%I OWNER TO %I'
        END;
        EXECUTE format(statement, item.nspname, item.relname, owner_role);
    END LOOP;

    FOR item IN
        SELECT namespace.nspname, procedure.proname,
               pg_get_function_identity_arguments(procedure.oid) AS arguments,
               procedure.prokind
        FROM pg_proc procedure
        JOIN pg_namespace namespace ON namespace.oid = procedure.pronamespace
        WHERE namespace.nspname = ANY(schema_names)
          AND procedure.prokind IN ('f', 'w', 'p', 'a')
          AND NOT EXISTS (
              SELECT 1 FROM pg_depend dependency
              WHERE dependency.classid = 'pg_proc'::regclass
                AND dependency.objid = procedure.oid
                AND dependency.deptype = 'e')
    LOOP
        statement := CASE item.prokind
            WHEN 'p' THEN format('ALTER PROCEDURE %I.%I(%s) OWNER TO %I', item.nspname, item.proname, item.arguments, owner_role)
            WHEN 'a' THEN format('ALTER AGGREGATE %I.%I(%s) OWNER TO %I', item.nspname, item.proname, item.arguments, owner_role)
            ELSE format('ALTER FUNCTION %I.%I(%s) OWNER TO %I', item.nspname, item.proname, item.arguments, owner_role)
        END;
        EXECUTE statement;
    END LOOP;

    FOR item IN
        SELECT namespace.nspname, type.typname, type.typtype
        FROM pg_type type
        JOIN pg_namespace namespace ON namespace.oid = type.typnamespace
        WHERE namespace.nspname = ANY(schema_names)
          AND type.typtype IN ('d', 'e', 'r')
          AND type.typrelid = 0
          AND NOT EXISTS (
              SELECT 1 FROM pg_depend dependency
              WHERE dependency.classid = 'pg_type'::regclass
                AND dependency.objid = type.oid
                AND dependency.deptype = 'e')
    LOOP
        statement := CASE item.typtype
            WHEN 'd' THEN format('ALTER DOMAIN %I.%I OWNER TO %I', item.nspname, item.typname, owner_role)
            ELSE format('ALTER TYPE %I.%I OWNER TO %I', item.nspname, item.typname, owner_role)
        END;
        EXECUTE statement;
    END LOOP;
END $$;
