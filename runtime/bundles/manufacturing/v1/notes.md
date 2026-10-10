# Manufacturing Demo ERP v1

This bundle is a lightweight make-to-order / job-shop manufacturing ERP demo. It proves that the ERP Generator runtime can model a second vertical beyond IT services and Teftero using the same folder-based YAML bundle primitives.

## Included

- Customers, vendors, employees, items, products, bills of material, warehouses, locations, stock balances, and stock movements.
- Sales orders and purchase orders with line-level computed totals.
- Work centers, routings, routing operations, work orders, material issues, production receipts, quality inspections, and shipments.
- Approval workflows for sales orders, purchase orders, work order release, and quality inspection.
- Declarative actions for creating work orders from sales orders, issuing work-order material intent, completing work orders, and receiving purchase orders.
- Reports and dashboard widgets for sales order status, open work orders, stock on hand, stock movements, purchase order status, quality inspections, production receipts, and low-stock balances.
- Demo seed data and acceptance tests for sales order to work order, production completion, purchase receipt, quality inspection, and reports.

## Intentionally Excluded

- Full MRP.
- Finite capacity scheduling.
- Barcode scanning.
- Serial and lot traceability.
- Advanced costing.
- Accounting ledger.
- Warehouse management.
- Procurement automation.
- CAD or PLM integrations.
- EDI.
- Shop-floor IoT.
- File or document attachments.

## Current Runtime Limitations

- `stock_balance` allows both `item` and `product` because the runtime does not yet support an XOR constraint.
- `issue_materials_to_work_order` does not automatically explode a BOM into material issue lines. It marks the work order in progress and records an audit event; users can create `material_issue` records manually.
- `complete_work_order` creates a `production_receipt`, but it does not update stock balances automatically.
- `receive_purchase_order` creates receipt `stock_movement` rows, but it does not update stock balances automatically.
- `items_at_or_below_reorder_point` uses a fixed demo threshold because report filters cannot yet compare one field to another, such as `quantity_on_hand <= reorder_point`.
- The bundle uses manual order numbers and PO numbers because there is no sequence-number primitive yet.
- There is no lot or serial tracking.

## Likely Next Primitive

The next generic runtime primitive for manufacturing should be an inventory ledger / stock adjustment action primitive. A useful shape would let declarative actions create stock movements and update stock balances transactionally from a typed movement definition.

A later primitive for BOM explosion or graph cloning would make `issue_materials_to_work_order` more realistic by turning active BOM lines into material issue requirements.
