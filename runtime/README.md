# ErpGenerator.Runtime

Shared .NET runtime/compiler for every generated ERP Generator ERP.

## Boundary

- Runtime resolves a tenant from the request `Host` header and connects to that tenant environment's isolated database.
- ErpGenerator.Api is the control plane and the only source for tenant/environment metadata, encrypted database credentials, canonical ERP Bundles, and deployment requests.
- Runtime owns bundle validation, compilation, generated schema/metadata, tenant-local identity, acceptance tests, and ERP execution.
- Runtime does not own marketing, customer accounts, discovery interviews, ERP generation, Nango source ingestion, or agent orchestration.

There is one shared Runtime service. Each tenant has one ERP database using its immutable stable tenant ID:

```text
erp_tenant_<stable_id>_erp
```

Tenant-local sessions are host-scoped. Portal/API credentials never authenticate a tenant Runtime user, and Runtime does not set wildcard domain cookies.

## Project structure

- `ErpGenerator.Runtime.Shared`: bundle model, validation, compiler, and typed cross-layer runtime contracts.
- `ErpGenerator.Runtime.Data`: tenant database connections, provisioning, backup, locking, and persistence implementations.
- `ErpGenerator.Runtime.Services`: application interfaces and implementations for runtime records, permissions, workflows, reports, users, deployment, control-plane communication, sessions, and the assistant.
- `ErpGenerator.Runtime.WebHost`: HTTP endpoints, host-scoped cookies, request/response mapping, HTML rendering, readiness, and hosted-process wiring.
- `ErpGenerator.Runtime.GeneratedPrimitives*`: trusted generated primitives and their source generator.

This is one modular monolith. Project dependencies point inward from WebHost to Services to Data, with Shared holding typed cross-layer contracts and deterministic compiler primitives. WebHost has direct references only to Services and Shared, transitive project references are disabled, and architecture tests reject a WebHost-to-Data assembly dependency or a Data type exposed by a service interface. No runtime microservices are introduced.

## Deployment flow

ErpGenerator.Api submits a canonical typed bundle to Runtime's private deployment broker. Runtime validates that it contains no raw SQL, compiles it, provisions the ERP database, applies the generated plan, and runs the supplied executable acceptance tests. API records readiness only after tests pass against the exact bundle and test suite. Portal opens the ready ERP through an ownership-checked guest handoff.

Existing databases retain their original stable-ID-derived names and hosts. They are not renamed or reseeded when switching to the single ERP environment.

The customer-facing tenant runtime uses a wildcard ingress only to preserve the Host header. The deployment broker remains private inside the cluster.

## AI-native foundation

The Runtime assistant is currently read-only and non-autonomous. Its answers are tied to the active bundle and validated tenant-local evidence, while future tools, permissions, previews, confirmations, line-item relationships, and proactive exception rules are defined as typed contracts. See [AI-native Runtime contracts](docs/ai-native-runtime-contracts.md) for the exact enabled/disabled boundary.

## Local validation

Copy `.env.example`, provide a dedicated PostgreSQL server, and run:

```bash
dotnet restore ErpGenerator.Runtime.sln
dotnet build ErpGenerator.Runtime.sln --no-restore
dotnet test ErpGenerator.Runtime.sln --no-build
dotnet run --project src/ErpGenerator.Runtime.WebHost
```

PostgreSQL-backed tests are skipped when their explicit test connection is unavailable; unit and contract tests still run.
