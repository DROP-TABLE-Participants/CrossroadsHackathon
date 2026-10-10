# ErpGenerator.Api instructions

This repository is the private .NET control-plane API and compact ERP-generation monolith.

- Keep it separate from ErpGenerator.Landing and never access the Landing/Payload database.
- Keep the API private. Customer routes are reachable only through ErpGenerator.Portal's allowlisted BFF; Runtime internal routes use a separate secret.
- Preserve layer direction: WebHost depends on Services, Services depends on Data, and Shared contains DTO contracts. Do not expose EF entities from controllers.
- Services use `ErpGeneratorDbContext` directly. Do not add repository or unit-of-work wrappers.
- Keep durable generation evidence-first: create interviews/pending work when important information is missing; do not silently generate a complete ERP from insufficient input.
- Keep generated bundles typed, validated, and free of raw SQL. Runtime owns compilation and tenant execution.
- Do not restore the former agent framework. Future specialist agents must be designed against the new boundaries.
- Every tenant environment uses a database name based on an immutable stable tenant ID.
- Use the EF Core CLI to create or remove migrations; never author migration files manually when the CLI is available.
- Run `dotnet restore`, `dotnet build`, and `dotnet test` before finishing changes.
