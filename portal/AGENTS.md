# ERP Generator Portal instructions

This repository owns the guest-first customer Next.js user interface only.

- Preserve the established account, authentication, interview, demo, feedback, and ERP lifecycle UX unless a UI change is explicitly requested.
- Keep business rules and persistence in `ErpGenerator.Api`; Portal has no database.
- Browser API calls must go through the deny-by-default BFF allowlist.
- Never expose `/api/internal`, generic operator, Runtime broker, provider-secret, or tenant-database endpoints through the BFF.
- Keep access and refresh tokens in host-only HttpOnly cookies. Never return them to browser JavaScript or persist them in localStorage.
- Keep `ERP_GENERATOR_API_INTERNAL_URL` and `ERP_GENERATOR_PORTAL_INTERNAL_SECRET` server-only.
- Keep English and Bulgarian customer-facing copy aligned.
- Use the existing Tailwind v4 and shadcn component system; do not add another design system.

Run `npm test` and `npm run build` before finishing.
