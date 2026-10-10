# Crossroads deployment

Portal: https://crossroads.dressr.fashion. Tenant ERPs use https://<tenant>.dressr.fashion.

Pushes to `main` run the existing validation, publish API, Runtime and Portal images with the same `dev-<full-source-SHA>` tag, then update the private `DROP-TABLE-Participants/Crossroads.Deployment` repository in one commit. Argo CD reconciles the three `crossroads-*` applications on `dressr-vm-k8s`.

The source Actions secret `DEPLOYMENT_REPO_SSH_KEY` is a write deploy key limited to that deployment repository. Argo CD uses a separate read-only key. Application credentials live only in Kubernetes Secrets; local deployment state is ignored under `.demo/deployment`.

Crossroads has its own API and Runtime PostgreSQL instances and persistent volumes. It uses the existing hosted Development configuration for the demo Runtime database policy, HTTPS host-only session cookies, Notion-only connectors, and server-side Claude generation and ERP assistance. The API remains private behind Portal's allowlisted BFF and Runtime's authenticated internal client.

The existing Proclaris tunnel serves the Portal and wildcard ERP routes. Existing Dressr DNS records keep their own destinations. `Runtime__ReservedTenantSlugs` reserves occupied hostnames so generated ERP names cannot collide with them.

Before presentation, check that the three Argo applications are Synced/Healthy, the migration succeeded, and the prepared Dandelion pool contains 20 validated copies. Use **Open demo ERP** for the presentation; **Reset demo** archives that visitor's copy and assigns a fresh validated copy. Customization creates a separate generated ERP.

Generate a QR code with `npm run demo:qr -- https://crossroads.dressr.fashion`. Local rehearsal remains available through the existing `.env.demo` commands.

Rollback by reverting the image-update commit in Crossroads.Deployment. Keep database volumes and records; do not run down migrations or delete PostgreSQL resources. Shared tunnel changes belong to Proclaris.Deployment and must preserve its existing routes.
