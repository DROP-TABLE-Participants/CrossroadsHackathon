# ERP Generator

This is one repository with three applications: `api`, `runtime`, and `portal`.
Follow each application's instructions in its `AGENTS.md`.

- Preserve the private API, Portal BFF allowlist, and tenant-local Runtime identity.
- Keep configuration keys, internal headers, cookies, namespaces, and project references consistent across components.
- Generation uses the native Claude Messages API with `claude-opus-5-5`. Provider credentials remain server-only.
- Use the EF Core CLI for creating or removing migrations when available.
- Run the root restore, build, and test commands before finishing implementation changes.
