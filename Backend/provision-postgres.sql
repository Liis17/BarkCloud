\set ON_ERROR_STOP on

SELECT set_config('barkcloud.isolation_mode', :'mode', false);

DO $$
BEGIN
    IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = current_user AND rolsuper) THEN
        RAISE EXCEPTION 'PostgreSQL provisioning requires a cluster superuser; use the infrastructure/operator credential.';
    END IF;
END $$;

SELECT format(
    'CREATE ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_configuration', :'configuration_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'barkcloud_configuration')
\gexec
SELECT format(
    'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_configuration', :'configuration_password')
\gexec

SELECT format(
    'CREATE ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_identity', :'identity_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'barkcloud_identity')
\gexec
SELECT format(
    'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_identity', :'identity_password')
\gexec

SELECT format(
    'CREATE ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_users', :'users_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'barkcloud_users')
\gexec
SELECT format(
    'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_users', :'users_password')
\gexec

SELECT format(
    'CREATE ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_files', :'files_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'barkcloud_files')
\gexec
SELECT format(
    'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_files', :'files_password')
\gexec

SELECT format(
    'CREATE ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_torrent', :'torrent_password')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'barkcloud_torrent')
\gexec
SELECT format(
    'ALTER ROLE %I WITH LOGIN PASSWORD %L NOSUPERUSER NOCREATEDB NOCREATEROLE NOBYPASSRLS NOREPLICATION NOINHERIT',
    'barkcloud_torrent', :'torrent_password')
\gexec

DO $$
DECLARE
    service_role record;
    inherited_role record;
    existing_database record;
BEGIN
    FOR service_role IN
        SELECT rolname FROM pg_roles
        WHERE rolname IN (
            'barkcloud_configuration', 'barkcloud_identity', 'barkcloud_users',
            'barkcloud_files', 'barkcloud_torrent')
    LOOP
        FOR inherited_role IN
            SELECT granted.rolname AS role_name
            FROM pg_auth_members membership
            JOIN pg_roles granted ON granted.oid = membership.roleid
            JOIN pg_roles member ON member.oid = membership.member
            WHERE member.rolname = service_role.rolname
        LOOP
            EXECUTE format('REVOKE %I FROM %I', inherited_role.role_name, service_role.rolname);
        END LOOP;
    END LOOP;

    FOR existing_database IN
        SELECT datname, pg_get_userbyid(datdba) AS owner_name
        FROM pg_database
        WHERE datname IN ('configuration', 'identity', 'users', 'files', 'torrent')
    LOOP
        IF current_setting('barkcloud.isolation_mode') = 'fresh'
           AND existing_database.owner_name <> ('barkcloud_' || existing_database.datname) THEN
            RAISE EXCEPTION 'Database % already exists with owner %. Use existing mode after backing up and stopping applications.',
                existing_database.datname, existing_database.owner_name;
        END IF;
    END LOOP;
END $$;

SELECT format('CREATE DATABASE %I OWNER %I', database_name, owner_name)
FROM (VALUES
    ('configuration', 'barkcloud_configuration'),
    ('identity', 'barkcloud_identity'),
    ('users', 'barkcloud_users'),
    ('files', 'barkcloud_files'),
    ('torrent', 'barkcloud_torrent')) AS databases(database_name, owner_name)
WHERE NOT EXISTS (SELECT 1 FROM pg_database WHERE datname = database_name)
\gexec

ALTER DATABASE configuration OWNER TO barkcloud_configuration;
ALTER DATABASE identity OWNER TO barkcloud_identity;
ALTER DATABASE users OWNER TO barkcloud_users;
ALTER DATABASE files OWNER TO barkcloud_files;
ALTER DATABASE torrent OWNER TO barkcloud_torrent;

REVOKE CONNECT, TEMPORARY ON DATABASE configuration FROM PUBLIC;
REVOKE CONNECT, TEMPORARY ON DATABASE identity FROM PUBLIC;
REVOKE CONNECT, TEMPORARY ON DATABASE users FROM PUBLIC;
REVOKE CONNECT, TEMPORARY ON DATABASE files FROM PUBLIC;
REVOKE CONNECT, TEMPORARY ON DATABASE torrent FROM PUBLIC;

REVOKE CONNECT, TEMPORARY ON DATABASE configuration FROM barkcloud_identity, barkcloud_users, barkcloud_files, barkcloud_torrent;
REVOKE CONNECT, TEMPORARY ON DATABASE identity FROM barkcloud_configuration, barkcloud_users, barkcloud_files, barkcloud_torrent;
REVOKE CONNECT, TEMPORARY ON DATABASE users FROM barkcloud_configuration, barkcloud_identity, barkcloud_files, barkcloud_torrent;
REVOKE CONNECT, TEMPORARY ON DATABASE files FROM barkcloud_configuration, barkcloud_identity, barkcloud_users, barkcloud_torrent;
REVOKE CONNECT, TEMPORARY ON DATABASE torrent FROM barkcloud_configuration, barkcloud_identity, barkcloud_users, barkcloud_files;

GRANT CONNECT, TEMPORARY ON DATABASE configuration TO barkcloud_configuration;
GRANT CONNECT, TEMPORARY ON DATABASE identity TO barkcloud_identity;
GRANT CONNECT, TEMPORARY ON DATABASE users TO barkcloud_users;
GRANT CONNECT, TEMPORARY ON DATABASE files TO barkcloud_files;
GRANT CONNECT, TEMPORARY ON DATABASE torrent TO barkcloud_torrent;

\connect configuration
\set app_role barkcloud_configuration
\set app_schemas public
\ir provision-postgres-ownership.sql

\connect identity
\set app_role barkcloud_identity
\set app_schemas public
\ir provision-postgres-ownership.sql

\connect users
\set app_role barkcloud_users
\set app_schemas public
\ir provision-postgres-ownership.sql

\connect files
CREATE SCHEMA IF NOT EXISTS files_quartz AUTHORIZATION barkcloud_files;
\set app_role barkcloud_files
\set app_schemas public,files_quartz
\ir provision-postgres-ownership.sql

\connect torrent
\set app_role barkcloud_torrent
\set app_schemas public
\ir provision-postgres-ownership.sql
