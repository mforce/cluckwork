using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // #283 Part 1 — roles/the default account/default egg grades/default
    // packed-unit conversions become static reference data. Deliberately raw
    // SQL (migrationBuilder.Sql), NOT EF's HasData()/InsertData: HasData is
    // keyed by PRIMARY KEY, which assumes a virgin schema. Every REAL
    // deployment already ran the old runtime DatabaseSeeder on every boot
    // (see AGENTS.md pre-#283 history), which wrote these same rows with:
    //   - the Account under the SAME fixed id this migration also uses
    //     (SeedDefaults.AccountId) — a plain InsertData would 23505 on
    //     PK_Accounts.
    //   - Roles/EggGrades/EggUnitConversions under RANDOM ids
    //     (RoleManager.CreateAsync(new ApplicationRole { Id = Guid.NewGuid(),
    //     ... }), EggGrade.Create(Guid.NewGuid(), ...),
    //     EggUnitConversion.Create(Guid.NewGuid(), ...)) — a plain InsertData
    //     using THIS migration's fixed ids would not collide on primary key,
    //     but would violate each table's real NATURAL-key unique constraint
    //     (RoleNameIndex on NormalizedName; IX_EggGrades_AccountId_FarmId_
    //     LowerName; IX_EggUnitConversions_AccountId_UnitCode) — a silent
    //     divergence, not a duplicate-PK, caught empirically by PR #339
    //     review applying this migration against a DB seeded the old way.
    //
    // So every INSERT below is a `WHERE NOT EXISTS` keyed on the table's
    // NATURAL key, not its primary key — a pre-existing row (under whatever
    // id the old seeder happened to mint) is left exactly as it is. Adopting
    // the existing row is the only safe choice: other tables (Flocks,
    // EggLots, DailyEntryGrades, ProductEggGradeMappings, AspNetUserRoles, …)
    // already reference these rows by FK, so rewriting an id out from under
    // them would be far more dangerous than leaving a "foreign" id in place.
    // A virgin database (no prior boot) has none of these rows, so every
    // INSERT below fires exactly once and the fixed ids ARE what lands.
    public partial class AddBaseReferenceDataAndMustChangePassword : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "MustChangePassword",
                table: "AspNetUsers",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            // --- Default account (natural key: Id — same fixed id both the
            // old seeder and this migration use, so this is a same-PK
            // existence check, not an adopt-a-different-id case).
            //
            // Gating choice: PER-KEY, which for a single row with a fixed
            // identity is the only meaningful option — there is no "set" that
            // could end up partially populated, so whole-set gating would say
            // exactly the same thing. The farm renames its account freely
            // (Settings), but Name is not the key here; Id is, and Id is not
            // user-mutable. ---
            migrationBuilder.Sql(
                """
                INSERT INTO "Accounts" ("Id", "AccountId", "Name", "TimeZoneId", "Locale", "DefaultCurrencyCode", "DefaultCurrencySymbol", "DefaultCurrencyMinorUnit", "UnitSystem", "FirstDayOfWeek", "DateFormatOverride", "TimeFormatOverride", "Brand", "IsActive", "Version")
                SELECT '0000000a-0000-0000-0000-000000000001', '0000000a-0000-0000-0000-000000000001', 'Default Farm', 'UTC', 'en-US', 'USD', '$', 2, 'Metric', NULL, NULL, NULL, 'aubergine', TRUE, 0
                WHERE NOT EXISTS (SELECT 1 FROM "Accounts" WHERE "Id" = '0000000a-0000-0000-0000-000000000001');
                """);

            // --- The four assignable roles (natural key: NormalizedName —
            // RoleNameIndex, unique).
            //
            // Gating choice: PER-KEY. Roles are genuinely static reference
            // data, not a user-managed catalog — there is no role CRUD
            // endpoint anywhere (the Users page ASSIGNS users to roles, it
            // never creates or renames them), and the names are compile-time
            // constants in Domain/Accounts/Roles.cs that AuthPolicies binds
            // to. So the key can never drift out from under this guard the
            // way a renamed grade does.
            //
            // Per-key is also the SAFER choice here: a missing role is a
            // broken-authorization bug, so an install that predates a role
            // being added must have it back-filled. Whole-set gating ("skip
            // everything if any role exists") would leave such an install
            // permanently missing that role — every existing database has at
            // least "Admin", so the whole batch would be skipped forever. ---
            migrationBuilder.Sql(
                """
                INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
                SELECT '0000000c-0000-0000-0000-000000000001', 'Admin', 'ADMIN', '0000000c-0000-0000-0000-000000000001'
                WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE "NormalizedName" = 'ADMIN');
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
                SELECT '0000000c-0000-0000-0000-000000000002', 'Manager', 'MANAGER', '0000000c-0000-0000-0000-000000000002'
                WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE "NormalizedName" = 'MANAGER');
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
                SELECT '0000000c-0000-0000-0000-000000000003', 'Sales', 'SALES', '0000000c-0000-0000-0000-000000000003'
                WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE "NormalizedName" = 'SALES');
                """);
            migrationBuilder.Sql(
                """
                INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
                SELECT '0000000c-0000-0000-0000-000000000004', 'ReadOnly', 'READONLY', '0000000c-0000-0000-0000-000000000004'
                WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE "NormalizedName" = 'READONLY');
                """);

            // --- The 10 default egg grades — WHOLE-SET gated (PR #339 review). ---
            //
            // This batch is the one exception to the per-natural-key rule
            // above, and the difference is USER-MUTABILITY OF THE KEY. The egg
            // grade catalog is user-managed: `PUT /api/v1/egg-grades/{id}`
            // (EggGrade.Update) RENAMES a grade. A farm that renamed the
            // seeded "Small" to "Pullet" still has that row — under a name a
            // per-name `WHERE NOT EXISTS (... lower("Name") = 'small')` guard
            // cannot see. Per-name gating would therefore INSERT a brand-new
            // active, saleable "Small" beside it: silently resurrecting a
            // default the farm deliberately renamed away, and changing what
            // appears in capture and order dropdowns.
            //
            // So the guard is "does this account have ANY grade at all", not
            // "does this particular name exist". That reproduces exactly what
            // the old runtime DatabaseSeeder did — it skipped the ENTIRE
            // default set once any grade existed, precisely because the
            // catalog becomes user-managed after first boot. The WHERE clause
            // does not reference `v`, so it is constant across the VALUES
            // list: either all 10 rows insert (virgin catalog) or none do
            // (existing catalog, left completely untouched — ids and all,
            // which matters because DailyEntryGrades / EggLots /
            // ProductEggGradeMappings hold FKs to them).
            //
            // Consequence accepted deliberately: a future default grade added
            // to this set will NOT reach farms that already have a catalog.
            // That is the correct trade for user-owned data — the alternative
            // re-introduces the resurrection bug. Such an addition needs its
            // own migration with its own explicit intent.
            migrationBuilder.Sql(
                """
                INSERT INTO "EggGrades" ("Id", "AccountId", "FarmId", "Name", "GradeType", "SortOrder", "IsSaleable", "Active", "Version")
                SELECT v.id::uuid, '0000000a-0000-0000-0000-000000000001', '0000000f-0000-0000-0000-000000000001',
                       v.name, v.grade_type, v.sort_order, v.is_saleable, TRUE, 0
                FROM (VALUES
                    ('0000000e-0000-0000-0000-000000000001', 'Small',        'Size',    0, TRUE),
                    ('0000000e-0000-0000-0000-000000000002', 'Medium',       'Size',    1, TRUE),
                    ('0000000e-0000-0000-0000-000000000003', 'Large',        'Size',    2, TRUE),
                    ('0000000e-0000-0000-0000-000000000004', 'Jumbo',        'Size',    3, TRUE),
                    ('0000000e-0000-0000-0000-000000000005', 'Seconds',      'Quality', 4, TRUE),
                    ('0000000e-0000-0000-0000-000000000006', 'Cracked',      'Quality', 5, FALSE),
                    ('0000000e-0000-0000-0000-000000000007', 'Dirty',        'Quality', 6, FALSE),
                    ('0000000e-0000-0000-0000-000000000008', 'Soft Shell',   'Quality', 7, FALSE),
                    ('0000000e-0000-0000-0000-000000000009', 'Discarded',    'Custom',  8, FALSE),
                    ('0000000e-0000-0000-0000-000000000010', 'Internal Use', 'Custom',  9, FALSE)
                ) AS v(id, name, grade_type, sort_order, is_saleable)
                WHERE NOT EXISTS (
                    SELECT 1 FROM "EggGrades"
                    WHERE "AccountId" = '0000000a-0000-0000-0000-000000000001'
                      AND "FarmId" = '0000000f-0000-0000-0000-000000000001');
                """);

            // --- The 6 default packed-unit conversions (natural key:
            // AccountId + UnitCode — IX_EggUnitConversions_AccountId_UnitCode,
            // a plain equality index; UnitCode's string form has no casing
            // ambiguity like Name does, so no lower() needed here).
            //
            // PER-KEY gating is correct here, unlike the grades above, because
            // the natural key is NOT user-mutable: EggUnitConversion.Update
            // only changes EggsPerUnit/Active, `UnitCode` has no setter and no
            // rename path (and `Individual` refuses Update outright), there is
            // no create endpoint and no delete endpoint for conversions. So a
            // farm can retune or deactivate a conversion but can never rename
            // or remove one — the "renamed row invisible to the guard" failure
            // that forces whole-set gating on grades simply cannot arise. A
            // deactivated row still EXISTS, so the guard sees it and leaves it
            // alone rather than resurrecting it active.
            //
            // Per-key is also the more useful choice: it back-fills a code an
            // older install predates, which is safe precisely because the set
            // of codes is fixed by the EggUnit enum the app already
            // understands, not by the farm. ---
            InsertUnitConversionIfMissing(migrationBuilder, "00000010-0000-0000-0000-000000000001", "Individual", 1);
            InsertUnitConversionIfMissing(migrationBuilder, "00000010-0000-0000-0000-000000000002", "Dozen", 12);
            InsertUnitConversionIfMissing(migrationBuilder, "00000010-0000-0000-0000-000000000003", "Flat", 30);
            InsertUnitConversionIfMissing(migrationBuilder, "00000010-0000-0000-0000-000000000004", "Tray", 30);
            InsertUnitConversionIfMissing(migrationBuilder, "00000010-0000-0000-0000-000000000005", "Carton", 12);
            InsertUnitConversionIfMissing(migrationBuilder, "00000010-0000-0000-0000-000000000006", "Case", 360);
        }

        private static void InsertUnitConversionIfMissing(
            MigrationBuilder migrationBuilder, string id, string unitCode, int eggsPerUnit)
        {
            migrationBuilder.Sql(
                $"""
                INSERT INTO "EggUnitConversions" ("Id", "AccountId", "UnitCode", "EggsPerUnit", "Active", "Version")
                SELECT '{id}', '0000000a-0000-0000-0000-000000000001', '{unitCode}', {eggsPerUnit}, TRUE, 0
                WHERE NOT EXISTS (
                    SELECT 1 FROM "EggUnitConversions"
                    WHERE "AccountId" = '0000000a-0000-0000-0000-000000000001' AND "UnitCode" = '{unitCode}');
                """);
        }

        /// <inheritdoc />
        // Deliberately does NOT delete the reference-data rows inserted above:
        // Down() has no way to tell "this row was inserted BY this migration"
        // from "this row already existed" (the whole point of the WHERE NOT
        // EXISTS guards), and other tables reference these rows by FK — a
        // delete-on-Down would risk cascading into real data or simply
        // failing on the FK constraint. Only the schema change (the new
        // column) is reversed; static reference data is a one-way seed.
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MustChangePassword",
                table: "AspNetUsers");
        }
    }
}
