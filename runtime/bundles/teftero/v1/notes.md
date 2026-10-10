# Teftero v1 Bundle Notes

This folder is a first-pass Teftero ERP Bundle skeleton. It intentionally uses only
the ERP Generator runtime primitives that already exist: entities, views, workflows,
declarative record actions, reports, dashboard widgets, roles, demo seeds, and
acceptance tests.

## Included in v1

- Foundation data: business categories, companies, contacts, employees, offices,
  organization structures, positions, service catalogue, and prospect statuses.
- Sales data: prospects, prospect activities, offers, offer milestones, offer
  price lines, team cost lines, additional cost lines, and offer comments.
- Contract data: contracts, contract milestones, contract price lines, contract
  team cost lines, contract additional cost lines, and contract comments.
- Delivery data: time entries, time-entry comments, tasks, subtasks, task
  dependencies, task comments, and subtask time logs.
- Workflows for time-entry approval and task completion.
- Actions to create an offer from a prospect and generate a contract structure
  from an offer.
- Reports and dashboard metrics for pipeline, offers, contracts, time, and tasks.
- Demo seed data and acceptance tests for the main v1 flows.

## Deferred old-Teftero parity features

These are not represented in this bundle because the current ERP Generator runtime
does not yet have the generic primitives needed for them:

- File uploads and document attachments.
- DOCX/PDF document template generation.
- CSV/Excel imports and exports.
- Iris, Entra ID, SMTP, and other external integrations.
- Custom wizard pages and workspace-specific UX.
- Human-readable numbering/sequences for offers, contracts, and tasks.
- Record-level permissions and ownership filters.
- Cross-entity analytical reports beyond one source entity.
- Comments/activity streams beyond regular comment entities and audit events.

## Action limitations

The action DSL now covers the source-record update needed by Teftero v1:
`create_offer_from_prospect` marks the prospect converted and links the generated
offer, and `generate_contract_from_offer` marks the offer generated and links the
generated contract. The remaining limitation is deeper graph clone/remap behavior:
contract rows can be copied from offer rows, but the runtime still does not have a
generic primitive for remapping arbitrary child graphs or creating human-readable
sequence numbers.
