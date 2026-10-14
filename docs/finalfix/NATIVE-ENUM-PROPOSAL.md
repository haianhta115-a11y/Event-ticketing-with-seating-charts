# T-15 native PostgreSQL enum — proposal, NOT executed

The backlog requires a database enum; the current contract is VARCHAR + CHECK.
No incompatible enum conversion is made against existing databases in FINAL-FIX.

Fresh-only proposal: `showtime_sales_status` values DRAFT, ONSALE, CLOSED, preserving
HTTP Draft/OnSale/Closed. Review Npgsql enum mapping, legacy SQL, EF model/snapshot,
history and rollback before implementing a schema change.

Separate schema approval is required for this rehearsal:

1. Create a new unique isolated local database using the existing guard.
2. Generate current model schema without historical seeds or EF history.
3. Check exact current_database, loopback PostgreSQL15, fresh initializer marker,
   model hash and zero showtimes. Reject all existing/protected/PRODUCT-01/restore
   targets, even if their showtimes table appears empty.
4. BEGIN; create the enum; convert only that newly created empty showtimes.status;
   inspect catalogs, test all transitions and HTTP serialization; ROLLBACK.
5. Compare complete schema/data/sequence/history inventory before and after.
6. Only with separate implementation approval, change the fresh EF mapping and
   initializer. Never edit historical migration rows or pretend migrations ran.

This document executes no SQL or migrations. The existing
`docs/fix01/sales-audit-schema.sql` is present and remains definition-only.
T-15 native-enum deployment/history reconciliation remains BLOCKED pending review;
its transition validation/audit behavior is verified independently.
