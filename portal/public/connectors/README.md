# Connector brand marks

Vendor logos used nominatively to identify each integration in the ERP creation
wizard and the account connectors panel. Each is the vendor's own mark, not a
redrawn approximation.

| File | Source |
| --- | --- |
| `google-drive.svg` | vectorlogo.zone — `logos/google_drive/google_drive-icon.svg` |
| `slack.svg` | vectorlogo.zone — `logos/slack/slack-icon.svg` |
| `notion.svg` | Wikimedia Commons — `Notion-logo.svg` |
| `sharepoint.svg` | Wikimedia Commons — `Microsoft Office SharePoint (2025–present).svg` |
| `microsoft-teams.svg` | Wikimedia Commons — `Microsoft Office Teams (2025–present).svg` |

Each file was normalized after download: intrinsic `width`/`height` stripped so
CSS controls the size, and a `viewBox` added where the source omitted one
(the vectorlogo.zone icons are authored in a 64×64 space).

Rendered by `src/components/account/ConnectorLogo.tsx`, keyed by the connector
catalog `key` in `src/components/account/helpers.ts`. Adding a connector means
dropping its SVG here and adding one line to `LOGO_FILES`.

These marks remain the trademarks of their respective owners. Keep usage
nominative — identifying the service being connected — and do not restyle,
recolour, or imply endorsement.
