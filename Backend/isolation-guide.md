# BarkCloud service credential isolation

The backend uses separate PostgreSQL roles and RabbitMQ users for Configuration, Identity, Users, Files, Torrent, and Notification. Their passwords are generated into `.env` by BarkCloud.Builder or `migrate-secrets.sh`. The fixed PostgreSQL role names are `barkcloud_configuration`, `barkcloud_identity`, `barkcloud_users`, `barkcloud_files`, and `barkcloud_torrent`; RabbitMQ users use the same service names, including `barkcloud_notification`.

`POSTGRES_HOST`/`POSTGRES_PORT` and `RABBITMQ_HOST`/`RABBITMQ_VIRTUAL_HOST` retain custom infrastructure addresses. A non-default RabbitMQ vhost must already exist before provisioning. `POSTGRES_USER` and `POSTGRES_PASSWORD` remain operator credentials for the PostgreSQL container and provisioning. `RABBITMQ_DEFAULT_USER` and `RABBITMQ_DEFAULT_PASS` remain broker bootstrap/operator credentials. Application containers do not receive those root credentials.

## Fresh installation

1. Generate or import the deployment with BarkCloud.Builder. For a copied sample, set operator credentials and other infrastructure/Web placeholders, then run `bash ./migrate-secrets.sh` to replace service/bootstrap placeholders and create the JWT key pair.
2. Start only the PostgreSQL and RabbitMQ infrastructure services and wait until both are ready: `docker compose up -d cloud-postgres cloud-rabbitmq`.
3. Create the service roles and permissions: `bash ./provision-isolation.sh fresh`.
4. Start the backend with the generated `docker-compose.yml` and `.env`: `docker compose up -d`.

Do not start backend applications before step 3. `--external` selects external PostgreSQL only: export `BARKCLOUD_POSTGRES_ADMIN_URL` without a password and set `POSTGRES_PASSWORD` in the process environment for the admin connection. For an external RabbitMQ broker, configure `rabbitmqctl` to target that node (including its Erlang cookie) and pass `--rabbitmq-external`; this prevents the script from selecting a local Compose broker. For example, use `bash ./provision-isolation.sh fresh --external --rabbitmq-external` when both systems are external. External provisioning requires `BARKCLOUD_APPS_STOPPED=YES`; for existing installations, add the same external-system flags to the `existing` command. Keep the generated scripts beside `.env`.

## Existing installation

This is an explicit maintenance transition. First make and verify PostgreSQL and RabbitMQ backups/definitions. Stop all application containers without removing volumes. Install the updated `docker-compose.yml`, `.env`, and the provisioning files together **before restarting any application**. Run `migrate-secrets.sh` to retain valid individual values and generate missing service passwords, then provision the existing data:

```sh
BARKCLOUD_BACKUP_VERIFIED=YES BARKCLOUD_APPS_STOPPED=YES bash ./provision-isolation.sh existing
```

The script refuses this mode without both acknowledgements and checks that Compose application services are stopped. It creates or updates the fixed service roles, moves ownership only within each application database's `public` schema and Files' `files_quartz` schema, and applies RabbitMQ permissions on the configured vhost. It never drops databases, queues, users, or roles. PostgreSQL extension-owned objects such as `pg_trgm` remain owned by the extension owner. It does not run EF migrations.

After provisioning, start the new Configuration image first. Its EF data migration removes legacy global and service-scoped RabbitMQ user/password values and the old root-credential database strings, including their settings history; startup then seeds per-service values from `.env`. Start the remaining new application images only after Configuration is healthy. Do not mix old and new backend images during the transition.

For a later service-password rotation, stop the applications, update the corresponding values in `.env`, rerun provisioning, and start Configuration before its consumers. Configuration refreshes its persisted deployment-managed database and RabbitMQ credentials on startup; other settings retain their configured values.

Rollback after provisioning requires stopping applications and restoring the previous compose file, `.env`, backend images, PostgreSQL backup, and RabbitMQ backup/definitions as a coordinated set. Restoring images alone is insufficient. Existing Identity refresh-session rows are not deleted by this migration; validate session behavior under the selected rollback/upgrade state rather than assuming they were invalidated.

## Trust boundary and smoke checks

Configuration is the trusted secrets authority and receives the service passwords needed to seed service-scoped settings. The Web container remains a trusted administrator: it intentionally receives the full `.env`, runs as root, and mounts `/var/run/docker.sock`. This deployment does not isolate Web compromise from infrastructure administration.

`postgres-isolation-smoke.sh` checks that each service role can connect to only its own application database and has no elevated role attributes. `rabbitmq-isolation-smoke.sh` checks the Identity-to-Notification email flow, an allowed Users event publish/consume, and Users/Files/Torrent denial for email exchange binding, reading, and publishing. Run these only against a disposable, isolated PostgreSQL/RabbitMQ deployment with applications stopped; the RabbitMQ test requires the management plugin, Python 3, and `rabbitmqctl`. It temporarily grants the service users the RabbitMQ `management` tag to call the HTTP API, then clears those tags on normal exit; resource ACLs remain active during the checks. The generated topology is left for inspection. Set `BARKCLOUD_ISOLATED_INFRA=YES`, `BARKCLOUD_APPS_STOPPED=YES`, export the service passwords, and for RabbitMQ set `BARKCLOUD_RABBITMQ_MANAGEMENT_URL`.
