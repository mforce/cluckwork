# Runbook: backup & restore (self-hosted)

**When to use this:** taking a disaster-recovery backup, or restoring one.

**Not this runbook:** exporting data for a spreadsheet or an offline copy — that
is the in-app **Export** screen, and it is not a restore format (see below).

**Blast radius:** the restore step is **destructive** — `--clean --if-exists`
drops and recreates every object it restores.

**Prerequisites:** shell access to the host running the compose stack, and
`deploy/.env`. Stop the API before restoring.

**Last drilled:** not recorded.

---

Two complementary layers (spec §17.5):

- **In-app**: an Admin can download any dataset as CSV — or the whole account as a
  zip — from the **Export** screen (`/api/v1/export/...`). Good for spreadsheets
  and keeping an offline copy; **not a restore format**.
- **Database dump**: the real backup for disaster recovery. This runbook.

## Backup

```bash
# Compressed custom format; uses the credentials from deploy/.env.
# -T is required: a pseudo-TTY would corrupt the binary dump.
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T db \
  sh -c 'pg_dump -U "$POSTGRES_USER" -Fc "$POSTGRES_DB"' > cluckwork-$(date +%Y%m%d).dump
```

## Verify the dump before trusting it

A dump that was never listed is a hypothesis.

```bash
pg_restore --list cluckwork-$(date +%Y%m%d).dump > /dev/null && echo OK
```

## Restore

> **Destructive.** `--clean --if-exists` drops each object before recreating it.
> Stop the API first — a serving process writing during a restore leaves a
> half-restored database. This cannot be undone except by restoring another dump.

```bash
docker compose -f deploy/docker-compose.yml --env-file deploy/.env exec -T db \
  sh -c 'pg_restore -U "$POSTGRES_USER" --clean --if-exists -d "$POSTGRES_DB"' < cluckwork-YYYYMMDD.dump
```

Start the API and confirm `/health/ready` returns 2xx — it 503s while any
migration is pending (#263), which is also how a restore of an older schema
announces itself.

## Handling dumps

Dumps contain everything — credential hashes, refresh tokens, every tenant — so
store them as **secrets**, not as shared files. Scheduled backups and backup
health checks are Phase 1.5 work, not shipped.

## Drill

Safe on a scratch stack only.

1. Take a backup as above; verify it with `pg_restore --list`.
2. Note a value you can recognise (a flock name, a customer).
3. Restore into a scratch database and confirm that value is present.
4. Boot the API against it; expect `/health/ready` 2xx.
5. Update **Last drilled** above.
