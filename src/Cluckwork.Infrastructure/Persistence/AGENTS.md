# AGENTS.md: `src/Cluckwork.Infrastructure/Persistence/`

Rules for EF Core persistence, migrations and the generated schema docs. The root [`AGENTS.md`](../../../AGENTS.md) and [`src/AGENTS.md`](../../AGENTS.md) apply too. [`CONTRIBUTING.md`](../../../CONTRIBUTING.md#changing-the-database-schema) has the migration commands.

## Migrations and schema

- One migration per change. Never edit `InitialCreate`. → [`407-migration-freeze.md`](../../../docs/decisions/407-migration-freeze.md)
- Base reference data (default account, roles, grades, unit conversions) ships as `migrationBuilder.Sql` with `WHERE NOT EXISTS` guards. Never use `HasData` or `InsertData`: EF later emits updates and deletes that revert the farm's own edits. → [`283-migrations-base-provisioning.md`](../../../docs/decisions/283-migrations-base-provisioning.md)
- Regenerate `docs/schema/` with `tools/schema-docs/generate.sh` after every migration and after a rebase conflict. Never hand-edit it. → [`417-schema-docs.md`](../../../docs/decisions/417-schema-docs.md)
- Do not time-partition `AuditEvents`. If it ever needs partitions, partition by `AccountId`. → [`505-audit-events-no-time-partition.md`](../../../docs/decisions/505-audit-events-no-time-partition.md)
