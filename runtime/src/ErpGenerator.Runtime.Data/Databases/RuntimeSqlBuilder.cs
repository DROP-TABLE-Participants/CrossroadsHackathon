using System.Text.Json;
using System.Text.RegularExpressions;
using ErpGenerator.Runtime.Shared.Compilation;

namespace ErpGenerator.Runtime.Data.Databases;

public sealed class RuntimeSqlBuilder
{
    private static readonly Regex IdentifierPattern = new("^[a-z][a-z0-9_]{1,62}$", RegexOptions.Compiled);

    public IReadOnlyList<string> BuildDeploymentStatements(CompiledBundle bundle)
    {
        var statements = new List<string>
        {
            "create schema if not exists _runtime",
            "create schema if not exists app",
            "create schema if not exists audit",
            """
            create table if not exists _runtime.bundle_versions (
              id bigserial primary key,
              bundle_id varchar(120) not null,
              bundle_name varchar(300) not null,
              version varchar(80) not null,
              bundle_hash varchar(128) not null,
              status varchar(40) not null default 'compiled',
              artifact_json jsonb,
              compiled_metadata jsonb,
              metadata jsonb not null,
              created_by text,
              created_at timestamptz not null default now(),
              staged_at timestamptz,
              production_at timestamptz,
              superseded_at timestamptz,
              rolled_back_at timestamptz,
              deployed_at timestamptz not null default now(),
              unique (bundle_id, version)
            )
            """,
            "alter table if exists _runtime.bundle_versions add column if not exists status varchar(40) not null default 'compiled'",
            "alter table if exists _runtime.bundle_versions add column if not exists artifact_json jsonb",
            "alter table if exists _runtime.bundle_versions add column if not exists compiled_metadata jsonb",
            "alter table if exists _runtime.bundle_versions add column if not exists created_by text",
            "alter table if exists _runtime.bundle_versions add column if not exists created_at timestamptz not null default now()",
            "alter table if exists _runtime.bundle_versions add column if not exists staged_at timestamptz",
            "alter table if exists _runtime.bundle_versions add column if not exists production_at timestamptz",
            "alter table if exists _runtime.bundle_versions add column if not exists superseded_at timestamptz",
            "alter table if exists _runtime.bundle_versions add column if not exists rolled_back_at timestamptz",
            """
            create table if not exists _runtime.bundle_migration_plans (
              id bigserial primary key,
              from_bundle_version_id bigint references _runtime.bundle_versions(id),
              to_bundle_version_id bigint not null references _runtime.bundle_versions(id),
              status varchar(40) not null,
              risk_level varchar(40) not null,
              operations jsonb not null,
              blocked_reasons jsonb not null default '[]'::jsonb,
              created_at timestamptz not null default now(),
              applied_at timestamptz
            )
            """,
            """
            create table if not exists _runtime.bundle_deployments (
              id bigserial primary key,
              bundle_version_id bigint references _runtime.bundle_versions(id),
              migration_plan_id bigint references _runtime.bundle_migration_plans(id),
              environment varchar(40) not null,
              status varchar(40) not null,
              started_at timestamptz not null default now(),
              finished_at timestamptz,
              error_message text,
              metadata jsonb not null default '{}'::jsonb
            )
            """,
            """
            create table if not exists _runtime.tenant_database_backups (
              id uuid primary key,
              tenant_id text,
              environment varchar(40) not null,
              database_name varchar(128) not null,
              backup_reason varchar(120) not null,
              created_by_operation varchar(120),
              backup_status varchar(40) not null,
              backup_path text,
              storage_mode varchar(40),
              storage_object_key text,
              storage_bucket text,
              size_bytes bigint,
              content_length bigint,
              sha256 varchar(128),
              created_at timestamptz not null default now(),
              completed_at timestamptz,
              error_message text,
              restore_status varchar(40),
              restore_target_database varchar(128),
              restored_at timestamptz,
              restore_error_message text,
              metadata jsonb not null default '{}'::jsonb
            )
            """,
            "alter table if exists _runtime.tenant_database_backups add column if not exists tenant_id text",
            "alter table if exists _runtime.tenant_database_backups add column if not exists created_by_operation varchar(120)",
            "alter table if exists _runtime.tenant_database_backups add column if not exists storage_mode varchar(40)",
            "alter table if exists _runtime.tenant_database_backups add column if not exists storage_object_key text",
            "alter table if exists _runtime.tenant_database_backups add column if not exists storage_bucket text",
            "alter table if exists _runtime.tenant_database_backups add column if not exists content_length bigint",
            "alter table if exists _runtime.tenant_database_backups add column if not exists sha256 varchar(128)",
            "alter table if exists _runtime.tenant_database_backups add column if not exists restore_status varchar(40)",
            "alter table if exists _runtime.tenant_database_backups add column if not exists restore_target_database varchar(128)",
            "alter table if exists _runtime.tenant_database_backups add column if not exists restored_at timestamptz",
            "alter table if exists _runtime.tenant_database_backups add column if not exists restore_error_message text",
            """
            create table if not exists _runtime.deployment_locks (
              id uuid primary key,
              lock_key varchar(220) not null,
              tenant_id text,
              environment varchar(40) not null,
              operation varchar(40) not null,
              owner_id varchar(120) not null,
              status varchar(40) not null,
              acquired_at timestamptz not null default now(),
              expires_at timestamptz not null,
              released_at timestamptz,
              metadata jsonb not null default '{}'::jsonb
            )
            """,
            """
            create unique index if not exists ux_deployment_locks_held
            on _runtime.deployment_locks(lock_key)
            where status = 'held'
            """,
            """
            create table if not exists _runtime.test_results (
              id bigserial primary key,
              bundle_version varchar(80) not null,
              bundle_hash varchar(128) not null,
              test_suite_hash varchar(64) not null,
              seed_hash varchar(64) not null default '',
              status varchar(40) not null,
              total integer not null,
              passed integer not null,
              failed integer not null,
              results jsonb not null,
              created_at timestamptz not null default now()
            )
            """,
            "alter table if exists _runtime.test_results add column if not exists bundle_hash varchar(128)",
            "alter table if exists _runtime.test_results add column if not exists test_suite_hash varchar(64)",
            "alter table if exists _runtime.test_results add column if not exists seed_hash varchar(64) not null default ''",
            """
            create table if not exists _runtime.active_bundle (
              id boolean primary key default true,
              bundle_version_id bigint not null references _runtime.bundle_versions(id),
              previous_bundle_version_id bigint references _runtime.bundle_versions(id),
              bundle_id varchar(120) not null,
              bundle_version varchar(80) not null,
              bundle_hash varchar(128) not null,
              activated_at timestamptz not null default now(),
              constraint active_bundle_singleton check (id)
            )
            """,
            """
            create table if not exists _runtime.entity_definitions (
              id varchar(120) primary key,
              bundle_id varchar(120) not null,
              module_id varchar(120) not null,
              name varchar(120) not null,
              display_name varchar(300) not null,
              table_name varchar(120) not null,
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.field_definitions (
              id varchar(180) primary key,
              entity_id varchar(120) not null,
              name varchar(120) not null,
              display_name varchar(300) not null,
              field_type varchar(80) not null,
              column_name varchar(120) not null,
              sql_type varchar(80) not null,
              is_required boolean not null,
              is_unique boolean not null,
              references_entity varchar(120),
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.view_definitions (
              id varchar(120) primary key,
              module_id varchar(120) not null,
              name varchar(300) not null,
              entity_id varchar(120) not null,
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.workflow_definitions (
              id varchar(120) primary key,
              module_id varchar(120) not null,
              name varchar(300) not null,
              entity_id varchar(120) not null,
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.schedule_definitions (
              id varchar(120) primary key,
              bundle_id varchar(120) not null,
              bundle_version varchar(80) not null,
              bundle_hash varchar(128) not null,
              entity_id varchar(120) not null,
              due_field varchar(120) not null,
              workflow_id varchar(120) not null,
              definition_hash varchar(64) not null,
              filters jsonb not null,
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.action_definitions (
              id varchar(120) primary key,
              label varchar(300) not null,
              entity_id varchar(120) not null,
              action_type varchar(80) not null,
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.report_definitions (
              id varchar(120) primary key,
              label varchar(300) not null,
              source_entity_id varchar(120) not null,
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.dashboard_definitions (
              id varchar(120) primary key,
              metadata jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.deployments (
              id bigserial primary key,
              bundle_id varchar(120) not null,
              bundle_version varchar(80) not null,
              bundle_hash varchar(128) not null,
              status varchar(80) not null,
              message text,
              created_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.runtime_users (
              id bigserial primary key,
              email varchar(320) not null unique,
              display_name varchar(300) not null,
              password_hash text,
              role varchar(80) not null,
              status varchar(40) not null default 'active',
              activated_at timestamptz,
              last_login_at timestamptz,
              created_at timestamptz not null default now()
            )
            """,
            "alter table if exists _runtime.runtime_users alter column password_hash drop not null",
            "alter table if exists _runtime.runtime_users add column if not exists status varchar(40) not null default 'active'",
            "alter table if exists _runtime.runtime_users add column if not exists activated_at timestamptz",
            "alter table if exists _runtime.runtime_users add column if not exists last_login_at timestamptz",
            "update _runtime.runtime_users set status = 'active' where status is null",
            """
            create table if not exists _runtime.role_definitions (
              id varchar(80) primary key,
              display_name varchar(160) not null,
              grants jsonb not null,
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.runtime_roles (
              id varchar(80) primary key,
              display_name varchar(160) not null,
              updated_at timestamptz not null default now()
            )
            """,
            $"""
            insert into _runtime.runtime_roles (id, display_name, updated_at)
            values ('{RuntimeSystemRoleIds.TenantOwner}', 'Tenant owner (system)', now())
            on conflict (id) do update set
              display_name = excluded.display_name,
              updated_at = now()
            """,
            """
            create table if not exists _runtime.runtime_user_roles (
              user_id bigint not null references _runtime.runtime_users(id) on delete cascade,
              role_id varchar(80) not null references _runtime.runtime_roles(id) on delete cascade,
              created_at timestamptz not null default now(),
              primary key (user_id, role_id)
            )
            """,
            """
            create table if not exists _runtime.activation_tokens (
              id bigserial primary key,
              token_hash varchar(128) not null unique,
              runtime_user_id bigint references _runtime.runtime_users(id) on delete cascade,
              email varchar(320),
              expires_at timestamptz not null,
              consumed_at timestamptz,
              created_by bigint,
              created_at timestamptz not null default now()
            )
            """,
            "alter table if exists _runtime.activation_tokens add column if not exists runtime_user_id bigint references _runtime.runtime_users(id) on delete cascade",
            "alter table if exists _runtime.activation_tokens add column if not exists created_by bigint",
            """
            create table if not exists _runtime.guest_entry_tokens (
              id bigserial primary key,
              token_hash varchar(64) not null unique,
              tenant_id varchar(128) not null,
              tenant_stable_id varchar(128) not null,
              environment varchar(24) not null,
              host varchar(253) not null,
              runtime_user_id bigint not null references _runtime.runtime_users(id) on delete cascade,
              expires_at timestamptz not null,
              consumed_at timestamptz,
              created_at timestamptz not null default now()
            )
            """,
            $"""
            update _runtime.runtime_users users
            set role = '{RuntimeSystemRoleIds.TenantOwner}'
            where lower(users.role) = 'owner'
              and exists (
                select 1
                from _runtime.activation_tokens token
                where token.runtime_user_id is null
                  and token.consumed_at is not null
                  and token.email is not null
                  and lower(token.email) = lower(users.email)
              )
            """,
            $"""
            insert into _runtime.runtime_user_roles (user_id, role_id)
            select users.id, '{RuntimeSystemRoleIds.TenantOwner}'
            from _runtime.runtime_users users
            where lower(users.role) = '{RuntimeSystemRoleIds.TenantOwner}'
            on conflict do nothing
            """,
            $"""
            delete from _runtime.runtime_user_roles roles
            using _runtime.runtime_users users
            where roles.user_id = users.id
              and lower(users.role) = '{RuntimeSystemRoleIds.TenantOwner}'
              and lower(roles.role_id) = 'owner'
            """,
            """
            create table if not exists _runtime.runtime_user_links (
              id bigserial primary key,
              runtime_user_id bigint not null references _runtime.runtime_users(id) on delete cascade,
              entity_key varchar(120) not null,
              record_id bigint not null,
              created_at timestamptz not null default now(),
              created_by bigint,
              unique (runtime_user_id, entity_key)
            )
            """,
            "alter table if exists _runtime.runtime_user_links drop constraint if exists runtime_user_links_entity_key_record_id_key",
            """
            create index if not exists ix_runtime_user_links_record
            on _runtime.runtime_user_links (entity_key, record_id)
            """,
            """
            create unique index if not exists ux_runtime_user_links_employee_record
            on _runtime.runtime_user_links (record_id)
            where lower(entity_key) in ('employee', 'employees')
            """,
            """
            create table if not exists _runtime.workflow_instances (
              id bigserial primary key,
              workflow_id varchar(120) not null,
              workflow_name varchar(300) not null,
              entity_id varchar(120) not null,
              entity_key varchar(120) not null,
              record_id bigint not null,
              status varchar(80) not null,
              current_step_id varchar(120),
              started_by bigint,
              completed_at timestamptz,
              metadata jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            )
            """,
            "alter table if exists _runtime.workflow_instances add column if not exists trigger_kind varchar(40) not null default 'action'",
            "alter table if exists _runtime.workflow_instances add column if not exists trigger_key varchar(120)",
            "alter table if exists _runtime.workflow_instances add column if not exists scheduler_job_id bigint",
            """
            create table if not exists _runtime.scheduled_jobs (
              id bigserial primary key,
              schedule_id varchar(120) not null,
              schedule_definition_hash varchar(64) not null,
              entity_id varchar(120) not null,
              record_id bigint not null,
              due_at timestamptz not null,
              status varchar(40) not null,
              attempt integer not null default 0,
              max_attempts integer not null default 5,
              available_at timestamptz not null,
              lease_id uuid,
              leased_by varchar(120),
              lease_expires_at timestamptz,
              workflow_instance_id bigint,
              acceptance_run_id uuid,
              last_error_code varchar(120),
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now(),
              completed_at timestamptz,
              constraint scheduled_jobs_attempt_bounds check (attempt >= 0 and max_attempts > 0 and attempt <= max_attempts),
              constraint scheduled_jobs_status check (status in ('ready', 'running', 'retry', 'completed', 'cancelled', 'superseded', 'exhausted')),
              unique (schedule_id, entity_id, record_id)
            )
            """,
            """
            create table if not exists _runtime.workflow_tasks (
              id bigserial primary key,
              instance_id bigint not null references _runtime.workflow_instances(id) on delete cascade,
              workflow_id varchar(120) not null,
              step_id varchar(120) not null,
              label varchar(300) not null,
              entity_id varchar(120) not null,
              record_id bigint not null,
              status varchar(80) not null,
              assigned_role varchar(80),
              assigned_user_id bigint,
              decision_by bigint,
              decision_at timestamptz,
              reason text,
              metadata jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now(),
              updated_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.workflow_events (
              id bigserial primary key,
              instance_id bigint not null references _runtime.workflow_instances(id) on delete cascade,
              task_id bigint,
              event_type varchar(120) not null,
              message text not null,
              metadata jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now()
            )
            """,
            """
            create table if not exists _runtime.notification_outbox (
              id bigserial primary key,
              instance_id bigint,
              task_id bigint,
              channel varchar(80) not null default 'runtime',
              notification_type varchar(120) not null default 'task_assigned',
              recipient_role varchar(80),
              recipient_user_id bigint,
              title text,
              message text,
              link text,
              subject text not null,
              payload jsonb not null default '{}'::jsonb,
              status varchar(80) not null default 'unread',
              read_at timestamptz,
              read_by bigint,
              created_at timestamptz not null default now()
            )
            """,
            "alter table if exists _runtime.notification_outbox add column if not exists notification_type varchar(120) not null default 'task_assigned'",
            "alter table if exists _runtime.notification_outbox add column if not exists title text",
            "alter table if exists _runtime.notification_outbox add column if not exists message text",
            "alter table if exists _runtime.notification_outbox add column if not exists link text",
            "alter table if exists _runtime.notification_outbox add column if not exists read_at timestamptz",
            "alter table if exists _runtime.notification_outbox add column if not exists read_by bigint",
            "update _runtime.notification_outbox set status = 'unread' where status = 'pending'",
            """
            create unique index if not exists ux_workflow_instances_active_record
            on _runtime.workflow_instances (workflow_id, entity_id, record_id)
            where status in ('running', 'waiting')
            """,
            """
            create unique index if not exists ux_workflow_instances_scheduler_job
            on _runtime.workflow_instances (scheduler_job_id)
            where scheduler_job_id is not null
            """,
            """
            create index if not exists ix_scheduled_jobs_ready
            on _runtime.scheduled_jobs (status, available_at, due_at, id)
            where status in ('ready', 'retry')
            """,
            """
            create index if not exists ix_scheduled_jobs_lease_expiry
            on _runtime.scheduled_jobs (lease_expires_at, id)
            where status = 'running'
            """,
            """
            create index if not exists ix_scheduled_jobs_acceptance_run
            on _runtime.scheduled_jobs (acceptance_run_id, id)
            where acceptance_run_id is not null
            """,
            """
            create index if not exists ix_workflow_tasks_pending_role
            on _runtime.workflow_tasks (status, assigned_role)
            """,
            """
            create index if not exists ix_workflow_tasks_instance
            on _runtime.workflow_tasks (instance_id)
            """,
            """
            create index if not exists ix_workflow_events_instance
            on _runtime.workflow_events (instance_id)
            """,
            """
            create index if not exists ix_notification_outbox_recipient_user
            on _runtime.notification_outbox (recipient_user_id, status)
            """,
            """
            create index if not exists ix_notification_outbox_recipient_role
            on _runtime.notification_outbox (recipient_role, status)
            """,
            """
            create table if not exists _runtime.assistant_interactions (
              id uuid primary key,
              runtime_user_id bigint not null,
              environment varchar(40) not null,
              bundle_hash varchar(128) not null,
              context jsonb not null,
              question text not null,
              answer text,
              model varchar(160),
              evidence jsonb not null default '[]'::jsonb,
              status varchar(40) not null,
              failure_code varchar(120),
              correlation_id varchar(120) not null,
              created_at timestamptz not null default now(),
              completed_at timestamptz
            )
            """,
            """
            create index if not exists ix_assistant_interactions_user_created
            on _runtime.assistant_interactions(runtime_user_id, created_at desc)
            """,
            """
            create table if not exists audit.events (
              id bigserial primary key,
              event_type varchar(120) not null,
              message text not null,
              metadata jsonb not null default '{}'::jsonb,
              created_at timestamptz not null default now()
            )
            """
        };

        foreach (var table in bundle.Tables)
        {
            statements.Add(BuildTableStatement(table));
            statements.AddRange(BuildColumnUpgradeStatements(table));
            statements.AddRange(table.Indexes.Select(index => BuildIndexStatement(table, index)));
        }
        statements.AddRange(bundle.Tables.SelectMany(table =>
            table.ForeignKeys.Select(foreignKey => BuildForeignKeyStatement(table, foreignKey))));

        statements.Add(BuildBundleUpsert(bundle));
        statements.AddRange(bundle.Entities.Select(entity => BuildEntityUpsert(bundle, entity)));
        statements.AddRange(bundle.Entities.SelectMany(entity => entity.Fields.Select(field => BuildFieldUpsert(entity, field))));
        statements.AddRange(bundle.Views.Select(BuildViewUpsert));
        statements.AddRange(bundle.Workflows.Select(BuildWorkflowUpsert));
        statements.AddRange(bundle.Schedules.Select(BuildScheduleJobSupersede));
        statements.AddRange(bundle.Schedules.Select(schedule => BuildScheduleUpsert(bundle, schedule)));
        statements.AddRange((bundle.Actions ?? []).Select(BuildActionUpsert));
        statements.AddRange(bundle.Reports.Select(BuildReportUpsert));
        statements.Add(BuildEntityPrune(bundle));
        statements.Add(BuildFieldPrune(bundle));
        statements.Add(BuildViewPrune(bundle));
        statements.Add(BuildWorkflowPrune(bundle));
        statements.Add(BuildRemovedScheduleJobSupersede(bundle));
        statements.Add(BuildSchedulePrune(bundle));
        statements.Add(BuildActionPrune(bundle));
        statements.Add(BuildReportPrune(bundle));
        statements.Add(BuildDashboardUpsert(bundle.Dashboard));
        statements.AddRange(bundle.Roles.SelectMany(role => new[] { BuildRoleUpsert(role), BuildRuntimeRoleUpsert(role) }));
        statements.Add(BuildRuntimeUserRoleBackfill());
        statements.Add(BuildDeploymentInsert(bundle, "deployed", "Sample ERP Bundle deployed by runtime compiler."));

        return statements;
    }

    private static string BuildTableStatement(TablePlan table)
    {
        EnsureIdentifier(table.SchemaName);
        EnsureIdentifier(table.TableName);

        var columns = table.Columns.Select(column =>
        {
            EnsureIdentifier(column.Name);
            var nullability = column.IsNullable ? string.Empty : " not null";
            var defaultValue = column.Name switch
            {
                "id" when column.SqlType == "bigserial" => " primary key",
                "created_at" => " default now()",
                "updated_at" => " default now()",
                _ => string.Empty
            };

            return $"{Quote(column.Name)} {column.SqlType}{defaultValue}{nullability}";
        });

        return $"create table if not exists {Quote(table.SchemaName)}.{Quote(table.TableName)} ({string.Join(", ", columns)})";
    }

    private static IEnumerable<string> BuildColumnUpgradeStatements(TablePlan table)
    {
        foreach (var column in table.Columns.Where(column => column.Name != "id"))
        {
            var nullability = column.IsSystem && !column.IsNullable ? " not null" : string.Empty;
            var defaultValue = column.Name switch
            {
                "created_at" => " default now()",
                "updated_at" => " default now()",
                _ => string.Empty
            };

            yield return $"alter table {Quote(table.SchemaName)}.{Quote(table.TableName)} add column if not exists {Quote(column.Name)} {column.SqlType}{defaultValue}{nullability}";
        }
    }

    private static string BuildIndexStatement(TablePlan table, IndexPlan index)
    {
        EnsureIdentifier(index.Name);
        foreach (var column in index.Columns)
        {
            EnsureIdentifier(column);
        }

        var unique = index.IsUnique ? "unique " : string.Empty;
        var columns = string.Join(", ", index.Columns.Select(Quote));
        return $"create {unique}index if not exists {Quote(index.Name)} on {Quote(table.SchemaName)}.{Quote(table.TableName)} ({columns})";
    }

    private static string BuildForeignKeyStatement(TablePlan table, ForeignKeyPlan foreignKey)
    {
        EnsureIdentifier(table.SchemaName);
        EnsureIdentifier(table.TableName);
        EnsureIdentifier(foreignKey.Name);
        EnsureIdentifier(foreignKey.Column);
        EnsureIdentifier(foreignKey.ReferencedSchema);
        EnsureIdentifier(foreignKey.ReferencedTable);
        EnsureIdentifier(foreignKey.ReferencedColumn);
        if (!foreignKey.OnDelete.Equals("restrict", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Foreign key '{foreignKey.Name}' must use restrictive deletion.");
        }

        return $"""
        do $erpgenerator$
        begin
          if not exists (
            select 1
            from pg_constraint
            where conname = {Literal(foreignKey.Name)}
              and conrelid = {Literal($"{table.SchemaName}.{table.TableName}")}::regclass
          ) then
            alter table {Quote(table.SchemaName)}.{Quote(table.TableName)}
              add constraint {Quote(foreignKey.Name)}
              foreign key ({Quote(foreignKey.Column)})
              references {Quote(foreignKey.ReferencedSchema)}.{Quote(foreignKey.ReferencedTable)} ({Quote(foreignKey.ReferencedColumn)})
              on delete restrict;
          end if;
        end
        $erpgenerator$
        """;
    }

    private static string BuildBundleUpsert(CompiledBundle bundle)
    {
        var metadata = JsonSerializer.Serialize(bundle);
        return $"""
        insert into _runtime.bundle_versions (bundle_id, bundle_name, version, bundle_hash, artifact_json, compiled_metadata, metadata)
        values ({Literal(bundle.BundleId)}, {Literal(bundle.BundleName)}, {Literal(bundle.Version)}, {Literal(bundle.BundleHash)}, {JsonLiteral(metadata)}, {JsonLiteral(metadata)}, {JsonLiteral(metadata)})
        on conflict (bundle_id, version) do nothing
        """;
    }

    private static string BuildEntityUpsert(CompiledBundle bundle, EntityDefinition entity)
    {
        var metadata = JsonSerializer.Serialize(entity);
        return $"""
        insert into _runtime.entity_definitions (id, bundle_id, module_id, name, display_name, table_name, metadata, updated_at)
        values ({Literal(entity.Id)}, {Literal(bundle.BundleId)}, {Literal(entity.ModuleId)}, {Literal(entity.Name)}, {Literal(entity.DisplayName)}, {Literal(entity.TableName)}, {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          bundle_id = excluded.bundle_id,
          module_id = excluded.module_id,
          name = excluded.name,
          display_name = excluded.display_name,
          table_name = excluded.table_name,
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildFieldUpsert(EntityDefinition entity, FieldDefinition field)
    {
        var metadata = JsonSerializer.Serialize(field);
        return $"""
        insert into _runtime.field_definitions (id, entity_id, name, display_name, field_type, column_name, sql_type, is_required, is_unique, references_entity, metadata, updated_at)
        values ({Literal($"{entity.Id}.{field.Id}")}, {Literal(entity.Id)}, {Literal(field.Name)}, {Literal(field.DisplayName)}, {Literal(field.Type)}, {Literal(field.ColumnName)}, {Literal(field.SqlType)}, {Bool(field.Required)}, {Bool(field.Unique)}, {NullableLiteral(field.References)}, {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          entity_id = excluded.entity_id,
          name = excluded.name,
          display_name = excluded.display_name,
          field_type = excluded.field_type,
          column_name = excluded.column_name,
          sql_type = excluded.sql_type,
          is_required = excluded.is_required,
          is_unique = excluded.is_unique,
          references_entity = excluded.references_entity,
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildViewUpsert(ViewDefinition view)
    {
        var metadata = JsonSerializer.Serialize(view);
        return $"""
        insert into _runtime.view_definitions (id, module_id, name, entity_id, metadata, updated_at)
        values ({Literal(view.Id)}, {Literal(view.ModuleId)}, {Literal(view.Name)}, {Literal(view.Entity)}, {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          module_id = excluded.module_id,
          name = excluded.name,
          entity_id = excluded.entity_id,
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildWorkflowUpsert(WorkflowDefinition workflow)
    {
        var metadata = JsonSerializer.Serialize(workflow);
        return $"""
        insert into _runtime.workflow_definitions (id, module_id, name, entity_id, metadata, updated_at)
        values ({Literal(workflow.Id)}, {Literal(workflow.ModuleId)}, {Literal(workflow.Name)}, {Literal(workflow.Entity)}, {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          module_id = excluded.module_id,
          name = excluded.name,
          entity_id = excluded.entity_id,
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildScheduleJobSupersede(ScheduleDefinition schedule) =>
        $"""
        update _runtime.scheduled_jobs
        set status = 'superseded',
            lease_id = null,
            leased_by = null,
            lease_expires_at = null,
            updated_at = now()
        where schedule_id = {Literal(schedule.Id)}
          and schedule_definition_hash <> {Literal(schedule.DefinitionHash)}
          and status in ('ready', 'retry', 'running', 'cancelled')
          and workflow_instance_id is null
        """;

    private static string BuildScheduleUpsert(CompiledBundle bundle, ScheduleDefinition schedule)
    {
        var filters = JsonSerializer.Serialize(schedule.Filters);
        var metadata = JsonSerializer.Serialize(schedule);
        return $"""
        insert into _runtime.schedule_definitions (
          id, bundle_id, bundle_version, bundle_hash, entity_id, due_field,
          workflow_id, definition_hash, filters, metadata, updated_at)
        values (
          {Literal(schedule.Id)}, {Literal(bundle.BundleId)}, {Literal(bundle.Version)},
          {Literal(bundle.BundleHash)}, {Literal(schedule.Entity)}, {Literal(schedule.DueField)},
          {Literal(schedule.Workflow)}, {Literal(schedule.DefinitionHash)},
          {JsonLiteral(filters)}, {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          bundle_id = excluded.bundle_id,
          bundle_version = excluded.bundle_version,
          bundle_hash = excluded.bundle_hash,
          entity_id = excluded.entity_id,
          due_field = excluded.due_field,
          workflow_id = excluded.workflow_id,
          definition_hash = excluded.definition_hash,
          filters = excluded.filters,
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildActionUpsert(ActionDefinition action)
    {
        var metadata = JsonSerializer.Serialize(action);
        return $"""
        insert into _runtime.action_definitions (id, label, entity_id, action_type, metadata, updated_at)
        values ({Literal(action.Key)}, {Literal(action.Label)}, {Literal(action.Entity)}, {Literal(action.Type)}, {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          label = excluded.label,
          entity_id = excluded.entity_id,
          action_type = excluded.action_type,
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildReportUpsert(ReportDefinition report)
    {
        var metadata = JsonSerializer.Serialize(report);
        return $"""
        insert into _runtime.report_definitions (id, label, source_entity_id, metadata, updated_at)
        values ({Literal(report.Key)}, {Literal(report.Label)}, {Literal(report.SourceEntity)}, {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          label = excluded.label,
          source_entity_id = excluded.source_entity_id,
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildDashboardUpsert(DashboardDefinition dashboard)
    {
        var metadata = JsonSerializer.Serialize(dashboard);
        return $"""
        insert into _runtime.dashboard_definitions (id, metadata, updated_at)
        values ('main', {JsonLiteral(metadata)}, now())
        on conflict (id) do update set
          metadata = excluded.metadata,
          updated_at = now()
        """;
    }

    private static string BuildWorkflowPrune(CompiledBundle bundle)
    {
        if (bundle.Workflows.Count == 0)
        {
            return "delete from _runtime.workflow_definitions";
        }

        return $"delete from _runtime.workflow_definitions where id not in ({string.Join(", ", bundle.Workflows.Select(workflow => Literal(workflow.Id)))})";
    }

    private static string BuildRemovedScheduleJobSupersede(CompiledBundle bundle)
    {
        var schedules = bundle.Schedules;
        var predicate = schedules.Count == 0
            ? string.Empty
            : $" and schedule_id not in ({string.Join(", ", schedules.Select(schedule => Literal(schedule.Id)))})";
        return $"""
        update _runtime.scheduled_jobs
        set status = 'superseded',
            lease_id = null,
            leased_by = null,
            lease_expires_at = null,
            updated_at = now()
        where status in ('ready', 'retry', 'running', 'cancelled')
          and workflow_instance_id is null{predicate}
        """;
    }

    private static string BuildSchedulePrune(CompiledBundle bundle)
    {
        if (bundle.Schedules.Count == 0)
        {
            return "delete from _runtime.schedule_definitions";
        }

        return $"delete from _runtime.schedule_definitions where id not in ({string.Join(", ", bundle.Schedules.Select(schedule => Literal(schedule.Id)))})";
    }

    private static string BuildActionPrune(CompiledBundle bundle)
    {
        var actions = bundle.Actions ?? [];
        if (actions.Count == 0)
        {
            return "delete from _runtime.action_definitions";
        }

        return $"delete from _runtime.action_definitions where id not in ({string.Join(", ", actions.Select(action => Literal(action.Key)))})";
    }

    private static string BuildEntityPrune(CompiledBundle bundle)
    {
        if (bundle.Entities.Count == 0)
        {
            return "delete from _runtime.entity_definitions";
        }

        return $"delete from _runtime.entity_definitions where id not in ({string.Join(", ", bundle.Entities.Select(entity => Literal(entity.Id)))})";
    }

    private static string BuildFieldPrune(CompiledBundle bundle)
    {
        var fieldIds = bundle.Entities
            .SelectMany(entity => entity.Fields.Select(field => $"{entity.Id}.{field.Id}"))
            .ToArray();
        if (fieldIds.Length == 0)
        {
            return "delete from _runtime.field_definitions";
        }

        return $"delete from _runtime.field_definitions where id not in ({string.Join(", ", fieldIds.Select(Literal))})";
    }

    private static string BuildViewPrune(CompiledBundle bundle)
    {
        if (bundle.Views.Count == 0)
        {
            return "delete from _runtime.view_definitions";
        }

        return $"delete from _runtime.view_definitions where id not in ({string.Join(", ", bundle.Views.Select(view => Literal(view.Id)))})";
    }

    private static string BuildReportPrune(CompiledBundle bundle)
    {
        if (bundle.Reports.Count == 0)
        {
            return "delete from _runtime.report_definitions";
        }

        return $"delete from _runtime.report_definitions where id not in ({string.Join(", ", bundle.Reports.Select(report => Literal(report.Key)))})";
    }

    private static string BuildRoleUpsert(RoleDefinition role)
    {
        var grants = JsonSerializer.Serialize(role.Grants);
        return $"""
        insert into _runtime.role_definitions (id, display_name, grants, updated_at)
        values ({Literal(role.Id)}, {Literal(role.DisplayName)}, {JsonLiteral(grants)}, now())
        on conflict (id) do update set
          display_name = excluded.display_name,
          grants = excluded.grants,
          updated_at = now()
        """;
    }

    private static string BuildRuntimeRoleUpsert(RoleDefinition role) =>
        $"""
        insert into _runtime.runtime_roles (id, display_name, updated_at)
        values ({Literal(role.Id)}, {Literal(role.DisplayName)}, now())
        on conflict (id) do update set
          display_name = excluded.display_name,
          updated_at = now()
        """;

    private static string BuildRuntimeUserRoleBackfill() =>
        """
        insert into _runtime.runtime_user_roles (user_id, role_id)
        select u.id, lower(u.role)
        from _runtime.runtime_users u
        join _runtime.runtime_roles r on r.id = lower(u.role)
        where u.role is not null and u.role <> ''
        on conflict do nothing
        """;

    private static string BuildDeploymentInsert(CompiledBundle bundle, string status, string message) =>
        $"""
        insert into _runtime.deployments (bundle_id, bundle_version, bundle_hash, status, message)
        values ({Literal(bundle.BundleId)}, {Literal(bundle.Version)}, {Literal(bundle.BundleHash)}, {Literal(status)}, {Literal(message)})
        """;

    private static void EnsureIdentifier(string value)
    {
        if (!IdentifierPattern.IsMatch(value))
        {
            throw new InvalidOperationException($"Unsafe SQL identifier '{value}'.");
        }
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string? NullableLiteral(string? value) => value is null ? "null" : Literal(value);

    private static string JsonLiteral(string value) => Literal(value) + "::jsonb";

    private static string Bool(bool value) => value ? "true" : "false";
}
