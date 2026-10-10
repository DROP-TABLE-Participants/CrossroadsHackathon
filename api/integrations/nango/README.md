# ERP Generator Nango Integrations

This folder is the Nango-side integration package owned and deployed by
`ErpGenerator.Api` for ERP Generator input ingestion.

It is intentionally separate from the .NET application projects inside the
repository. Provider-specific logic belongs here, in Nango functions, while
the API consumes a small set of stable unified input models through its generic
Nango ingestion service and freezes them into generation evidence snapshots.

Current scope:

- Google Drive, SharePoint, and Notion document-content syncs,
- Slack and Microsoft Teams message syncs,
- `UnifiedDocument` and `UnifiedMessage` output contracts,
- local TypeScript type checks and fixture tests.

Notion is deliberately scope-scoped. The connection metadata contains a
`notionScope` rule with either `all_accessible` or `selected_roots` mode,
and the sync fails closed when that rule is absent. `all_accessible` includes
everything available to the connection. Each `selected_roots` page always
includes its subpages, each selected database includes its rows, and new
content inside a selected area is included automatically. The sync follows
provider pagination without a customer content-count ceiling, saves records in
batches, checkpoints progress, tracks upstream deletions, and records
individual extraction failures without discarding the rest of a successful
sync. The content browser exhausts provider cursors into a transient hierarchy
and uses Notion child-block order; listing available pages or databases does
not persist their titles or content in the ERP Generator database.

The Google Drive sync lists file metadata and retrieves authoritative text
content through Drive `files.export` (Docs, Sheets, and Slides) or
`files.get?alt=media` (text-like blobs). The SharePoint sync discovers
authorized sites and document libraries through Microsoft Graph, walks bounded
folder trees, downloads bounded text files, and extracts text from bounded
DOCX/PPTX/XLSX Open XML archives. Unsupported binary formats remain explicit
non-evidence records. Tests do not use live credentials or deploy functions.
Delegated connections discover the signed-in user's followed sites first;
broader site search and the root site are bounded fallbacks. Configure only the
minimum Graph scopes needed for the selected tenant/site policy (`Files.Read`
and, where site discovery requires it, `Sites.Read.All`).

## Shape

Nango's current local-functions convention uses a TypeScript integrations folder with a root `index.ts` entrypoint and per-integration folders such as:

```text
integrations/nango/
  index.ts
  google-drive/syncs/documents.ts
  microsoft-teams/syncs/messages.ts
  notion/syncs/documents.ts
  sharepoint-online/syncs/documents.ts
  slack/syncs/messages.ts
```

Testable mapping/extraction logic lives under `src/files/*`; deployment-facing
Nango wrappers stay thin and persist records with `batchSave`.

## UnifiedDocument Contract

Provider functions should output:

```ts
export type UnifiedDocument = {
  id: string;
  title: string;
  text: string;
  contentState: "content_extracted" | "metadata_only" | "unsupported" | "extraction_failed";
  sourceUrl?: string;
  provider: string;
  mimeType?: string;
  modifiedAt?: string;
  author?: string;
  metadata?: Record<string, unknown>;
};
```

Only `content_extracted` records with non-empty text become ERP Generator
`InputDocument` evidence. Metadata-only, unsupported, and failed extraction
records remain diagnosable in Nango but are excluded from evidence; a required
source containing no authoritative text fails closed.

## Data lifecycle

- Connecting requires an explicit authorization acknowledgement recorded by the
  API with a versioned notice.
- Notion content is not synchronized until the customer confirms either all
  content shared with the connection or one or more selected root areas.
- Nango remains the credential and provider-access boundary. ERP Generator stores
  normalized records only after a completed one-off sync, never OAuth tokens.
- Normalized connector records expire after 30 days by default and are removed
  immediately when the source is disconnected. The API retention worker
  enforces this independently of Nango's own cache retention.
- Evidence already frozen into a generation snapshot follows the project
  lifecycle and is removed when that project is deleted. The Portal discloses
  this before authorization and again before disconnecting.
- Connector records may be sent to the Claude API only as project evidence.
  Generation uses native Messages requests; provider retention follows the
  configured Anthropic account terms.

## Local Validation

Install dependencies:

```bash
npm install
```

Run local checks:

```bash
npm run typecheck
npm test
```

The tests use static fixtures and fake provider responses only:

- `src/files/google-drive/fixtures/google-drive-files.json`
- `src/files/sharepoint/fixtures/sharepoint-files.json`
- `tests/fixtures/expected-unified-documents.json`

## Nango CLI

The Nango CLI is not required for local validation. Use the configured Nango
deployment workflow to publish all five provider syncs; do not commit
`nango-integrations/.env`.

The `ErpGenerator.Api` dev workflow validates and automatically deploys this
package after the API validation job. It expects `NANGO_SECRET_KEY_DEV`.
Configure provider configs
with these exact keys before the first deployment: `google-drive`,
`microsoft-teams`, `notion`, `sharepoint-online`, and `slack`. Use least-privilege
read scopes: Drive file read access; Microsoft Graph `User.Read`,
`Team.ReadBasic.All`, `Channel.ReadBasic.All`, and `ChannelMessage.Read.All`
for Teams; the selected Notion pages shared with the integration; SharePoint
`Files.Read` plus `Sites.Read.All` only when site discovery needs it; and Slack
conversation read/history scopes for the conversation types enabled in the
workspace.

Configure the Nango dev environment webhook destination as
`https://portal.erpgenerator.example/api/integrations/nango/webhook`. The Portal
forwards only this signed route to the private API; Cloudflare must not expose
the API directly. The Nango webhook signing key must match
`Nango__WebhookSigningKey` in the API secret. A fully local Portal is not
internet-reachable, so it performs one reconciliation when the developer
returns from the Nango authorization tab instead of polling continuously.

## Other Families

The same pattern should be used for later connector families:

- issues -> `UnifiedIssue`,
- CRM -> `UnifiedCustomer`,
- accounting -> `UnifiedInvoice`.

Do not add provider-specific C# adapters for each external API.
