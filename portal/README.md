# ERP Generator Portal

The guest-first customer web application for ERP Generator. Visitors enter their
workspace automatically and can open a prepared Dandelion Chocolate demo or
create a separate ERP using a description, guided interview, and optional Notion
context. Business state and decisions remain in `ErpGenerator.Api`.

Portal has no database. Browser requests go through a deny-by-default Next.js BFF
allowlist with customer and public routes. Internal Runtime and operator endpoints
are never exposed. Customer access and refresh tokens use host-only HttpOnly
cookies. Existing browser sessions resume their workspace; Runtime receives its
own tenant-local session through the ownership-checked handoff.

## Local setup

```bash
npm ci
cp .env.example .env
npm run dev
```

Portal runs on [http://localhost:3001](http://localhost:3001) when started with `PORT=3001 npm run dev`. Start `ErpGenerator.Api` on the URL configured by `ERP_GENERATOR_API_INTERNAL_URL`.

## Configuration

- `ERP_GENERATOR_API_INTERNAL_URL` is server-only and should resolve to the API ClusterIP in Kubernetes.
- `ERP_GENERATOR_PORTAL_INTERNAL_SECRET` authenticates BFF requests to the API and must be shared only by Portal and API.
- `ERP_GENERATOR_LANDING_PUBLIC_URL` is used when account UI links back to marketing or legal content.
- `FRONTEND_URL` is the exact public origin accepted for state-changing BFF requests.
- `ERP_GENERATOR_COOKIE_SECURE=false` is accepted only for localhost development; deployed cookies remain Secure.
- `NEXT_PUBLIC_SITE_URL` is the public Portal origin.
- `NEXT_PUBLIC_CALCOM_*` values optionally override demo-booking hosts.

No database, Claude, Nango provider credential, Runtime broker credential, or tenant database secret belongs in Portal.
