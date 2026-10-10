# ErpGenerator.Api

Private control-plane API and compact ERP generation service for ErpGenerator.

## Boundary

- **ErpGenerator.Portal** is the public guest-first customer web application and the only gateway to customer API routes.
- **ErpGenerator.Api** owns customer accounts, tenants, discovery, interviews, durable generation, bundle lineage, deployment orchestration, feedback, and usage metadata.
- **ErpGenerator.Runtime** owns compilation, isolated tenant databases, tenant-local identity, and generated ERP execution.
- **ErpGenerator.Landing** owns marketing content only. This API never reads or writes the Landing database.

All `/api/*` routes require the Portal gateway secret except `/api/internal/*`, which use the separate Runtime internal secret. The Kubernetes service must remain private; neither route family should receive a public ingress.

## Layers

- `ErpGenerator.Api.Data`: EF Core entities, `ErpGeneratorDbContext`, mappings, and migrations.
- `ErpGenerator.Api.Services`: business services and durable workers. Services use the DbContext directly; there is no repository layer.
- `ErpGenerator.Api.WebHost`: controllers, authentication, middleware, and hosting.
- `ErpGenerator.Api.MigrationService`: one-shot EF migration and customer Identity-role initialization.
- `ErpGenerator.Api.Shared`: input/view models and cross-process contracts; EF entities are never exposed.

Dependency direction is `WebHost/MigrationService -> Services -> Data`, with `Shared` used for contracts.

## Customer identity

Visitors receive a synthetic guest account and a customer session automatically.
Customer accounts, roles, refresh sessions, and JWTs use `ErpGeneratorDbContext`
and the `CustomerPortalUser` policy. Account status and security stamps are
validated on every accepted access token. Portal stores tokens only in host-only
HttpOnly cookies; Runtime establishes its own tenant-local identity through the
private, ownership-checked handoff broker.

MigrationService initializes the customer schema and Customer role. There is no
platform administration login or bootstrap requirement.

## Generation behavior

One compact durable pipeline first assesses whether the supplied evidence is sufficient. If important facts are missing, it creates pending evidence requests or interviews and waits. Only then does it generate a typed ERP Bundle with executable acceptance tests. The bundle cannot contain raw SQL and Runtime performs compilation and staging validation before production promotion.

Each model call is bound to the exact request hash. Retrying unchanged input reuses the durable result, while an interview answer, customer resolution, document, or connected-source update creates a new evidence snapshot and therefore a new discovery assessment. A resumed run never reuses a completed call against different evidence.

Production promotion completes only when API verifies Runtime's canonical receipt payload and its HMAC signature. `RuntimeBroker__PromotionReceiptVerificationKey` must match Runtime's `RuntimeDeploymentBroker__PromotionReceiptSigningKey`; this key is not reused for broker authentication or lifecycle attestations.

The former multi-agent implementation is intentionally not carried forward. Specialized agents can be rebuilt later behind the same service contracts.

## Local development

The workspace-level `./dev-up` command is the recommended path. It starts PostgreSQL, runs MigrationService to completion, and starts WebHost only after migration succeeds.

For direct development, export the values from `.env`, start PostgreSQL, then run:

```bash
dotnet run --project src/ErpGenerator.Api.MigrationService
dotnet run --project src/ErpGenerator.Api.WebHost
```

WebHost never applies migrations. Use the EF Core CLI to create migration source files, and use MigrationService to apply them.

The control plane has one CLI-generated `InitialControlPlane` migration and one
model snapshot. MigrationService creates the current schema for a fresh database.
Before rebaselining an existing database, back it up and verify the retained
schema, then replace its `public.__EFMigrationsHistory` entry with the current
initial migration ID. Retired identity/audit tables are not used by the application;
rebaselining must preserve existing customer, project, demo, and tenant records.

Portal should use `http://localhost:3001`, while API defaults to `http://localhost:8080`. Configure the same `PortalGateway__Secret` in API and `ERP_GENERATOR_PORTAL_INTERNAL_SECRET` in Portal.

## Validation

```bash
dotnet restore ErpGenerator.Api.sln
dotnet build ErpGenerator.Api.sln --no-restore
dotnet test ErpGenerator.Api.sln --no-build
docker build .
```
