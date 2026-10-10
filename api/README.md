# ErpGenerator.Api

Private control-plane API and compact ERP generation service for ErpGenerator.

## Boundary

- **ErpGenerator.Portal** is the public customer/admin web application and the only gateway to customer API routes.
- **ErpGenerator.Api** owns customer accounts, tenants, discovery, interviews, durable generation, bundle lineage, deployment orchestration, feedback, and usage metadata.
- **ErpGenerator.Runtime** owns compilation, isolated tenant databases, tenant-local identity, and generated ERP execution.
- **ErpGenerator.Landing** owns marketing content only. This API never reads or writes the Landing database.

All `/api/*` routes require the Portal gateway secret except `/api/internal/*`, which use the separate Runtime internal secret. The Kubernetes service must remain private; neither route family should receive a public ingress.

## Layers

- `ErpGenerator.Api.Data`: EF Core entities, `ErpGeneratorDbContext`, mappings, and migrations.
- `ErpGenerator.Api.Services`: business services and durable workers. Services use the DbContext directly; there is no repository layer.
- `ErpGenerator.Api.WebHost`: controllers, authentication, middleware, and hosting.
- `ErpGenerator.Api.MigrationService`: one-shot EF migration, Identity-role initialization, and optional development-admin bootstrap.
- `ErpGenerator.Api.Shared`: input/view models and cross-process contracts; EF entities are never exposed.

Dependency direction is `WebHost/MigrationService -> Services -> Data`, with `Shared` used for contracts.

## Identity realms

Customer and platform-administrator identities are independent security realms:

- customer accounts, roles, refresh sessions, and JWTs use `ErpGeneratorDbContext`, the customer issuer/audience/key, and the `CustomerPortalUser` policy;
- administrator accounts, roles, refresh sessions, and JWTs use `AdminIdentityDbContext`, the `admin_identity` schema, a separate issuer/audience/key, and the `PlatformAdministrator` policy;
- the same email address may exist in both stores without linking the accounts, passwords, roles, reset tokens, or sessions;
- account status and security-stamp validation run on every accepted access token, so deactivation or password reset invalidates existing access and refresh sessions.

The migration service initializes the administrator store first and refuses the
cutover unless at least one active administrator exists. For the first run,
enable `AdminBootstrap` with an explicit email and strong password, verify the
new administrator login, then disable bootstrap. It subsequently removes the
legacy `Admin` role from the customer store and revokes those legacy sessions.

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

Each DbContext now has one initial migration: `InitialControlPlane` and
`InitialAdminIdentity`. MigrationService creates both schemas for a new database.
For an existing database that matches the current model, back up and verify its
schema before replacing the corresponding history entries in
`public.__EFMigrationsHistory` and `admin_identity.__EFMigrationsHistory` with the
new initial migration IDs. This preserves its application tables and records.

Portal should use `http://localhost:3001`, while API defaults to `http://localhost:8080`. Configure the same `PortalGateway__Secret` in API and `ERP_GENERATOR_PORTAL_INTERNAL_SECRET` in Portal.

## Validation

```bash
dotnet restore ErpGenerator.Api.sln
dotnet build ErpGenerator.Api.sln --no-restore
dotnet test ErpGenerator.Api.sln --no-build
docker build .
```
