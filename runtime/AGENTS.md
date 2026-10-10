# ErpGenerator.Runtime instructions

This repository owns the shared ERP runtime/compiler only.

- Keep a .NET modular monolith. Do not create one service per tenant or split Runtime into microservices.
- Preserve layer direction while refactoring: WebHost depends on Services, Services depends on Data, and Shared contains cross-layer contracts. GeneratedPrimitives remains trusted technical support.
- Resolve tenants from the Host header, never a path segment.
- Every tenant environment has an isolated database named from its immutable stable tenant ID.
- Keep Runtime identity tenant-local and separate from Portal/API identity. Do not use wildcard domain cookies.
- ErpGenerator.Api is the only control plane. Access it through authenticated internal HTTP APIs; never access Landing/Payload or the API database directly.
- Accept only typed canonical ERP Bundles. Reject raw SQL and arbitrary generated code.
- Use one ERP environment per tenant. Compile and pass the exact bundle acceptance tests before granting access.
- Keep sample bundles only as explicit compiler/runtime fixtures, never as a hidden fallback for real generation.
- This repo does not own discovery, interviews, customer accounts, source ingestion, generation agents, or model orchestration.
- Run `dotnet restore`, `dotnet build`, and `dotnet test` before finishing.
