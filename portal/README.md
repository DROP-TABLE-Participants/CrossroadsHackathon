# ERP Generator Portal

The customer and administrator web application for ErpGenerator. It preserves the existing account, ERP-discovery, interview, demo-booking, feedback, and environment-management UX while keeping all business state and decisions in `ErpGenerator.Api`.

Portal has no database and no Payload CMS dependency. Browser requests go through a deny-by-default Next.js BFF route. Every allowlisted route belongs to exactly one of the customer, administrator, or public realms; an administrator cookie is never forwarded to a customer route, and a customer cookie is never forwarded to an administrator route. Internal Runtime endpoints are never exposed.

Customer sign-in lives at `/{locale}/login` and administrator sign-in at
`/{locale}/admin/login`. They use distinct host-only HttpOnly access and refresh
cookies, independent refresh endpoints, and independent password-reset flows.
Holding an administrator session does not grant or redirect into customer
account pages.

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
