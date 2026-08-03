using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Cluckwork.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    // #245 — the SQUASH. This replaces the 34 migrations that accumulated
    // between 2026-06-27 and 2026-08-01 (12 of which carried raw SQL written
    // for mid-history data states — backfills that touch 0 rows on an empty
    // database but still execute). It is generated from the CURRENT model and
    // produces the same schema those 34 produced; the equivalence was proved
    // by applying both to a throwaway Postgres and diffing
    // `pg_dump --schema-only`.
    //
    // Safe to do exactly once, and only now: no production database exists,
    // so no `__EFMigrationsHistory` anywhere needs baselining. Any database
    // that already has the OLD history rows (a dev DB from
    // deploy/docker-compose.dev.yml, a demo/sim instance) can no longer be
    // migrated forward — drop and recreate it:
    //   docker compose -f deploy/docker-compose.dev.yml down -v && up -d
    //
    // Two categories of difference against the pre-squash schema were
    // accepted deliberately, both invisible to the application:
    //   - COLUMN ORDER. The old schema's physical column order was an
    //     artifact of the order columns were ADDed over three weeks; this one
    //     is model order. Postgres attaches no semantics to it, and nothing
    //     in this repo does SELECT * / INSERT-without-a-column-list.
    //   - VESTIGIAL COLUMN DEFAULTS. `AddColumn(defaultValue: ...)` leaves a
    //     permanent DEFAULT on the column purely so the backfill of existing
    //     rows had a value (e.g. Accounts."Locale" DEFAULT 'en-US',
    //     SalesOrderItems."Unit" DEFAULT ''). They were never in the EF model
    //     — the snapshot has never recorded them — so EF always wrote those
    //     columns explicitly and never relied on them. They are not
    //     reproduced here; the only DB-level defaults that remain are the
    //     ones the model actually declares (refresh_tokens."RevokedByGrace",
    //     and — folded in afterward, see the #364 note below —
    //     AspNetUsers."CredentialEpoch" and refresh_tokens."IssuedEpoch").
    // Everything else — every table, column type/nullability/length, PK, FK
    // with its delete behaviour, unique and non-unique index (including the
    // four lower(Name) expression indexes hand-carried below), and the
    // Version concurrency tokens — is byte-identical.
    //
    // #364 — ADDENDUM, folded in the same way and for the same reason before
    // this repo was ever deployed. Credential-epoch revocation added four
    // columns (AspNetUsers.CredentialEpoch/DisabledAt/DisabledBy,
    // refresh_tokens.IssuedEpoch). A virgin database was still the only
    // starting state that existed anywhere, so these were hand-folded
    // directly into this migration rather than shipped as a second one —
    // the #245 reasoning applies again verbatim: no `__EFMigrationsHistory`
    // anywhere needs baselining, so there was never anything to "add a
    // migration" on top of. The original #364 patch also carried an
    // `UPDATE refresh_tokens SET "RevokedAt" = now() WHERE "RevokedAt" IS
    // NULL` cutover backfill (plus a `SET LOCAL lock_timeout` to bound the
    // ADD COLUMN lock); both were dropped in the fold — the backfill always
    // touches zero rows here, precisely the dead-backfill anti-pattern #245
    // squashed away, and the lock guard exists only to protect a rolling
    // upgrade over pre-existing traffic that likewise cannot exist yet.
    // MigrationSecurityReviewTests pins that this directory still holds
    // exactly one migration.
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Accounts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    TimeZoneId = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Locale = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DefaultCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    DefaultCurrencySymbol = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: true),
                    DefaultCurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    UnitSystem = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FirstDayOfWeek = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    DateFormatOverride = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    TimeFormatOverride = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    Brand = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Accounts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoles", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUsers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayName = table.Column<string>(type: "text", nullable: true),
                    Language = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    MustChangePassword = table.Column<bool>(type: "boolean", nullable: false),
                    UserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedUserName = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: true),
                    EmailConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    PasswordHash = table.Column<string>(type: "text", nullable: true),
                    SecurityStamp = table.Column<string>(type: "text", nullable: true),
                    ConcurrencyStamp = table.Column<string>(type: "text", nullable: true),
                    PhoneNumber = table.Column<string>(type: "text", nullable: true),
                    PhoneNumberConfirmed = table.Column<bool>(type: "boolean", nullable: false),
                    TwoFactorEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LockoutEnabled = table.Column<bool>(type: "boolean", nullable: false),
                    AccessFailedCount = table.Column<int>(type: "integer", nullable: false),
                    // #364 — credential-epoch revocation. CredentialEpoch starts at 1 and
                    // RefreshToken.IssuedEpoch (below) starts at 0, so a missing/malformed
                    // credential_epoch claim (which CredentialEpochMiddleware maps to 0)
                    // can never equal a live user's epoch. Load-bearing: do not change
                    // either default. DisabledAt/DisabledBy are schema-only here — no
                    // mutation sets them yet.
                    CredentialEpoch = table.Column<int>(type: "integer", nullable: false, defaultValue: 1),
                    DisabledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DisabledBy = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUsers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AuditEvents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OccurredAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DetailsJson = table.Column<string>(type: "text", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AuditEvents", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Customers",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Phone = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: true),
                    Address = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Customers", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "DailyEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalEggs = table.Column<int>(type: "integer", nullable: false),
                    CrackedEggs = table.Column<int>(type: "integer", nullable: false),
                    DirtyEggs = table.Column<int>(type: "integer", nullable: false),
                    DiscardedEggs = table.Column<int>(type: "integer", nullable: false),
                    MortalityCount = table.Column<int>(type: "integer", nullable: false),
                    AdjustReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    AdjustedFromJson = table.Column<string>(type: "text", nullable: true),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    LockedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "durable_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    RunAfter = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "text", nullable: true),
                    Attempts = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_durable_jobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EggGrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    GradeType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    IsSaleable = table.Column<bool>(type: "boolean", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EggGrades", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "EggUnitConversions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UnitCode = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EggsPerUnit = table.Column<int>(type: "integer", nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EggUnitConversions", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ExpenseCategories",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExpenseCategories", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "FarmLogos",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Content = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    ByteLength = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FarmLogos", x => x.Id);
                    table.CheckConstraint("ck_farm_logos_content_length", "octet_length(\"Content\") > 0 AND octet_length(\"Content\") <= 5242880");
                });

            migrationBuilder.CreateTable(
                name: "Flocks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    HouseId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Breed = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PlacementDate = table.Column<DateOnly>(type: "date", nullable: false),
                    InitialCount = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    DepletedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    ArchivedOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Flocks", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "idempotency_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    EndpointHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<int>(type: "integer", nullable: false),
                    LeaseOwner = table.Column<Guid>(type: "uuid", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StatusCode = table.Column<int>(type: "integer", nullable: true),
                    ContentType = table.Column<string>(type: "text", nullable: true),
                    ResponseBody = table.Column<string>(type: "text", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_idempotency_records", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "InventoryItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Category = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    DefaultCostMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    DefaultCostCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: true),
                    DefaultCostCurrencyMinorUnit = table.Column<int>(type: "integer", nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryItems", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "Products",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    ProductType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DefaultUnit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    DefaultPriceMinorUnits = table.Column<long>(type: "bigint", nullable: true),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    Notes = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Active = table.Column<bool>(type: "boolean", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Products", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "refresh_tokens",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReplacedByTokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    RevokedByGrace = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ConcurrencyStamp = table.Column<string>(type: "character varying(36)", maxLength: 36, nullable: false),
                    // #364 — see the matching comment on AspNetUsers.CredentialEpoch above.
                    // Every known mint site stamps this explicitly to the user's current
                    // epoch; the 0 default is a defense-in-depth backstop for a writer
                    // that doesn't, and 0 is permanently invalid (no user is ever epoch 0).
                    IssuedEpoch = table.Column<int>(type: "integer", nullable: false, defaultValue: 0)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "simulation_seed_state",
                columns: table => new
                {
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false),
                    Anchor = table.Column<DateOnly>(type: "date", nullable: false),
                    CompletedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_simulation_seed_state", x => x.AccountId);
                });

            migrationBuilder.CreateTable(
                name: "UserRoleAssignments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: true),
                    HouseId = table.Column<Guid>(type: "uuid", nullable: true),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_UserRoleAssignments", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "AspNetRoleClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetRoleClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetRoleClaims_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserClaims",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClaimType = table.Column<string>(type: "text", nullable: true),
                    ClaimValue = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserClaims", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AspNetUserClaims_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserLogins",
                columns: table => new
                {
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    ProviderKey = table.Column<string>(type: "text", nullable: false),
                    ProviderDisplayName = table.Column<string>(type: "text", nullable: true),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserLogins", x => new { x.LoginProvider, x.ProviderKey });
                    table.ForeignKey(
                        name: "FK_AspNetUserLogins_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserRoles",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    RoleId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserRoles", x => new { x.UserId, x.RoleId });
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetRoles_RoleId",
                        column: x => x.RoleId,
                        principalTable: "AspNetRoles",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_AspNetUserRoles_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AspNetUserTokens",
                columns: table => new
                {
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LoginProvider = table.Column<string>(type: "text", nullable: false),
                    Name = table.Column<string>(type: "text", nullable: false),
                    Value = table.Column<string>(type: "text", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AspNetUserTokens", x => new { x.UserId, x.LoginProvider, x.Name });
                    table.ForeignKey(
                        name: "FK_AspNetUserTokens_AspNetUsers_UserId",
                        column: x => x.UserId,
                        principalTable: "AspNetUsers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrders",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OrderDate = table.Column<DateOnly>(type: "date", nullable: false),
                    TotalMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    TotalCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    TotalCurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrders", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrders_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "DailyEntryGrades",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    DailyEntryId = table.Column<Guid>(type: "uuid", nullable: false),
                    EggGradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DailyEntryGrades", x => x.Id);
                    table.ForeignKey(
                        name: "FK_DailyEntryGrades_DailyEntries_DailyEntryId",
                        column: x => x.DailyEntryId,
                        principalTable: "DailyEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_DailyEntryGrades_EggGrades_EggGradeId",
                        column: x => x.EggGradeId,
                        principalTable: "EggGrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "EggLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductionDate = table.Column<DateOnly>(type: "date", nullable: false),
                    EggGradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    QuantityProduced = table.Column<int>(type: "integer", nullable: false),
                    QuantityAvailable = table.Column<int>(type: "integer", nullable: false),
                    DailyEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    RestrictedUntil = table.Column<DateOnly>(type: "date", nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EggLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EggLots_EggGrades_EggGradeId",
                        column: x => x.EggGradeId,
                        principalTable: "EggGrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "BirdMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DailyEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BirdMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BirdMovements_DailyEntries_DailyEntryId",
                        column: x => x.DailyEntryId,
                        principalTable: "DailyEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BirdMovements_Flocks_FlockId",
                        column: x => x.FlockId,
                        principalTable: "Flocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "Expenses",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FarmId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExpenseCategoryId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Description = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Expenses", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Expenses_ExpenseCategories_ExpenseCategoryId",
                        column: x => x.ExpenseCategoryId,
                        principalTable: "ExpenseCategories",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Expenses_Flocks_FlockId",
                        column: x => x.FlockId,
                        principalTable: "Flocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "WaterUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    Source = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    MeterStart = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    MeterEnd = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DailyEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_WaterUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_WaterUsages_DailyEntries_DailyEntryId",
                        column: x => x.DailyEntryId,
                        principalTable: "DailyEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_WaterUsages_Flocks_FlockId",
                        column: x => x.FlockId,
                        principalTable: "Flocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "FeedUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Quantity = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    EstimatedCostMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    EstimatedCostCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    EstimatedCostCurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    DailyEntryId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_FeedUsages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_FeedUsages_DailyEntries_DailyEntryId",
                        column: x => x.DailyEntryId,
                        principalTable: "DailyEntries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedUsages_Flocks_FlockId",
                        column: x => x.FlockId,
                        principalTable: "Flocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_FeedUsages_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryLots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    ReceivedDate = table.Column<DateOnly>(type: "date", nullable: false),
                    LotNumber = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ExpiryDate = table.Column<DateOnly>(type: "date", nullable: true),
                    QuantityReceived = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    QuantityAvailable = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    UnitCostMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    UnitCostCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    UnitCostCurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryLots", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryLots_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ProductEggGradeMappings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    EggGradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProductEggGradeMappings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ProductEggGradeMappings_EggGrades_EggGradeId",
                        column: x => x.EggGradeId,
                        principalTable: "EggGrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ProductEggGradeMappings_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Payments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uuid", nullable: false),
                    PaymentDate = table.Column<DateOnly>(type: "date", nullable: false),
                    AmountMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    CurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    CurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    Method = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ReferenceNumber = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Voided = table.Column<bool>(type: "boolean", nullable: false),
                    VoidReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Payments", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Payments_Customers_CustomerId",
                        column: x => x.CustomerId,
                        principalTable: "Customers",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Payments_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderItems",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductId = table.Column<Guid>(type: "uuid", nullable: false),
                    ProductTypeSnapshot = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    EggGradeId = table.Column<Guid>(type: "uuid", nullable: false),
                    Unit = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    BaseUnitFactor = table.Column<int>(type: "integer", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    QuantityBase = table.Column<int>(type: "integer", nullable: false),
                    UnitPriceMinorUnits = table.Column<long>(type: "bigint", nullable: false),
                    UnitPriceCurrencyCode = table.Column<string>(type: "character varying(3)", maxLength: 3, nullable: false),
                    UnitPriceCurrencyMinorUnit = table.Column<int>(type: "integer", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderItems", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrderItems_EggGrades_EggGradeId",
                        column: x => x.EggGradeId,
                        principalTable: "EggGrades",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderItems_Products_ProductId",
                        column: x => x.ProductId,
                        principalTable: "Products",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderItems_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "EggInventoryMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EggLotId = table.Column<Guid>(type: "uuid", nullable: false),
                    MovementType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QuantityDelta = table.Column<int>(type: "integer", nullable: false),
                    ReferenceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    ReferenceId = table.Column<Guid>(type: "uuid", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_EggInventoryMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_EggInventoryMovements_EggLots_EggLotId",
                        column: x => x.EggLotId,
                        principalTable: "EggLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "InventoryMovements",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    InventoryLotId = table.Column<Guid>(type: "uuid", nullable: true),
                    Date = table.Column<DateOnly>(type: "date", nullable: false),
                    Type = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    QuantityDelta = table.Column<decimal>(type: "numeric(18,3)", precision: 18, scale: 3, nullable: false),
                    Unit = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FlockId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedAtUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ReferenceType = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    ReferenceId = table.Column<Guid>(type: "uuid", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_InventoryMovements", x => x.Id);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_Flocks_FlockId",
                        column: x => x.FlockId,
                        principalTable: "Flocks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_InventoryItems_InventoryItemId",
                        column: x => x.InventoryItemId,
                        principalTable: "InventoryItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_InventoryMovements_InventoryLots_InventoryLotId",
                        column: x => x.InventoryLotId,
                        principalTable: "InventoryLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SalesOrderAllocations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderId = table.Column<Guid>(type: "uuid", nullable: false),
                    SalesOrderItemId = table.Column<Guid>(type: "uuid", nullable: false),
                    EggLotId = table.Column<Guid>(type: "uuid", nullable: false),
                    Quantity = table.Column<int>(type: "integer", nullable: false),
                    ReleasedOnUtc = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    AccountId = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SalesOrderAllocations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_SalesOrderAllocations_EggLots_EggLotId",
                        column: x => x.EggLotId,
                        principalTable: "EggLots",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SalesOrderAllocations_SalesOrderItems_SalesOrderItemId",
                        column: x => x.SalesOrderItemId,
                        principalTable: "SalesOrderItems",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SalesOrderAllocations_SalesOrders_SalesOrderId",
                        column: x => x.SalesOrderId,
                        principalTable: "SalesOrders",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AspNetRoleClaims_RoleId",
                table: "AspNetRoleClaims",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "RoleNameIndex",
                table: "AspNetRoles",
                column: "NormalizedName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserClaims_UserId",
                table: "AspNetUserClaims",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserLogins_UserId",
                table: "AspNetUserLogins",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_AspNetUserRoles_RoleId",
                table: "AspNetUserRoles",
                column: "RoleId");

            migrationBuilder.CreateIndex(
                name: "EmailIndex",
                table: "AspNetUsers",
                column: "NormalizedEmail");

            migrationBuilder.CreateIndex(
                name: "UserNameIndex",
                table: "AspNetUsers",
                column: "NormalizedUserName",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_AccountId_EntityId",
                table: "AuditEvents",
                columns: new[] { "AccountId", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_AuditEvents_AccountId_OccurredAtUtc",
                table: "AuditEvents",
                columns: new[] { "AccountId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_BirdMovements_AccountId_FlockId_Date",
                table: "BirdMovements",
                columns: new[] { "AccountId", "FlockId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_BirdMovements_DailyEntryId",
                table: "BirdMovements",
                column: "DailyEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_BirdMovements_FlockId",
                table: "BirdMovements",
                column: "FlockId");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_AccountId_Name",
                table: "Customers",
                columns: new[] { "AccountId", "Name" });

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntries_NaturalKey",
                table: "DailyEntries",
                columns: new[] { "AccountId", "FarmId", "HouseId", "FlockId", "Date" },
                unique: true,
                filter: "\"Status\" <> 'Voided'");

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntryGrades_AccountId",
                table: "DailyEntryGrades",
                column: "AccountId");

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntryGrades_DailyEntryId_EggGradeId",
                table: "DailyEntryGrades",
                columns: new[] { "DailyEntryId", "EggGradeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_DailyEntryGrades_EggGradeId",
                table: "DailyEntryGrades",
                column: "EggGradeId");

            migrationBuilder.CreateIndex(
                name: "IX_durable_jobs_Status_RunAfter",
                table: "durable_jobs",
                columns: new[] { "Status", "RunAfter" });

            migrationBuilder.CreateIndex(
                name: "IX_EggInventoryMovements_AccountId_EggLotId_CreatedAtUtc",
                table: "EggInventoryMovements",
                columns: new[] { "AccountId", "EggLotId", "CreatedAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_EggInventoryMovements_EggLotId",
                table: "EggInventoryMovements",
                column: "EggLotId");

            migrationBuilder.CreateIndex(
                name: "IX_EggLots_Allocation",
                table: "EggLots",
                columns: new[] { "AccountId", "EggGradeId", "ProductionDate", "QuantityAvailable" });

            migrationBuilder.CreateIndex(
                name: "IX_EggLots_DailyEntryId",
                table: "EggLots",
                column: "DailyEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_EggLots_EggGradeId",
                table: "EggLots",
                column: "EggGradeId");

            migrationBuilder.CreateIndex(
                name: "IX_EggUnitConversions_AccountId_UnitCode",
                table: "EggUnitConversions",
                columns: new[] { "AccountId", "UnitCode" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_AccountId_Date",
                table: "Expenses",
                columns: new[] { "AccountId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_AccountId_ExpenseCategoryId",
                table: "Expenses",
                columns: new[] { "AccountId", "ExpenseCategoryId" });

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_ExpenseCategoryId",
                table: "Expenses",
                column: "ExpenseCategoryId");

            migrationBuilder.CreateIndex(
                name: "IX_Expenses_FlockId",
                table: "Expenses",
                column: "FlockId");

            migrationBuilder.CreateIndex(
                name: "IX_FarmLogos_AccountId_FarmId",
                table: "FarmLogos",
                columns: new[] { "AccountId", "FarmId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_FeedUsages_DailyEntryId",
                table: "FeedUsages",
                column: "DailyEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_FeedUsages_FlockId_Date",
                table: "FeedUsages",
                columns: new[] { "FlockId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_FeedUsages_InventoryItemId_Date",
                table: "FeedUsages",
                columns: new[] { "InventoryItemId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_Flocks_AccountId_FarmId_HouseId",
                table: "Flocks",
                columns: new[] { "AccountId", "FarmId", "HouseId" });

            migrationBuilder.CreateIndex(
                name: "IX_idempotency_records_AccountId_EndpointHash_IdempotencyKeyHa~",
                table: "idempotency_records",
                columns: new[] { "AccountId", "EndpointHash", "IdempotencyKeyHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_InventoryItems_AccountId_FarmId",
                table: "InventoryItems",
                columns: new[] { "AccountId", "FarmId" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryLots_InventoryItemId_ReceivedDate",
                table: "InventoryLots",
                columns: new[] { "InventoryItemId", "ReceivedDate" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_FlockId",
                table: "InventoryMovements",
                column: "FlockId");

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_InventoryItemId_Date",
                table: "InventoryMovements",
                columns: new[] { "InventoryItemId", "Date" });

            migrationBuilder.CreateIndex(
                name: "IX_InventoryMovements_InventoryLotId",
                table: "InventoryMovements",
                column: "InventoryLotId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AccountId_CustomerId",
                table: "Payments",
                columns: new[] { "AccountId", "CustomerId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_AccountId_SalesOrderId",
                table: "Payments",
                columns: new[] { "AccountId", "SalesOrderId" });

            migrationBuilder.CreateIndex(
                name: "IX_Payments_CustomerId",
                table: "Payments",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_Payments_SalesOrderId",
                table: "Payments",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductEggGradeMappings_EggGradeId",
                table: "ProductEggGradeMappings",
                column: "EggGradeId");

            migrationBuilder.CreateIndex(
                name: "IX_ProductEggGradeMappings_ProductId",
                table: "ProductEggGradeMappings",
                column: "ProductId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_ExpiresAt",
                table: "refresh_tokens",
                column: "ExpiresAt");

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_TokenHash",
                table: "refresh_tokens",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_refresh_tokens_UserId",
                table: "refresh_tokens",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderAllocations_EggLotId",
                table: "SalesOrderAllocations",
                column: "EggLotId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderAllocations_SalesOrderId",
                table: "SalesOrderAllocations",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderAllocations_SalesOrderItemId",
                table: "SalesOrderAllocations",
                column: "SalesOrderItemId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderItems_EggGradeId",
                table: "SalesOrderItems",
                column: "EggGradeId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderItems_ProductId",
                table: "SalesOrderItems",
                column: "ProductId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrderItems_SalesOrderId",
                table: "SalesOrderItems",
                column: "SalesOrderId");

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_AccountId_ReferenceNumber",
                table: "SalesOrders",
                columns: new[] { "AccountId", "ReferenceNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SalesOrders_CustomerId",
                table: "SalesOrders",
                column: "CustomerId");

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_AccountId_UserId",
                table: "UserRoleAssignments",
                columns: new[] { "AccountId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_UserRoleAssignments_UserId_FlockId",
                table: "UserRoleAssignments",
                columns: new[] { "UserId", "FlockId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_WaterUsages_DailyEntryId",
                table: "WaterUsages",
                column: "DailyEntryId");

            migrationBuilder.CreateIndex(
                name: "IX_WaterUsages_FlockId_Date",
                table: "WaterUsages",
                columns: new[] { "FlockId", "Date" });

            // ---------------------------------------------------------------
            // Carried forward by hand from the squashed history (#245).
            // Everything above this line is regenerated from the EF model;
            // everything below it CANNOT be, and would be silently lost by a
            // plain `dotnet ef migrations add`.
            // ---------------------------------------------------------------

            // 1. Case-insensitive unique names. Expression (functional)
            // indexes aren't representable in the EF model, so they live as
            // raw SQL — verbatim from AddEggGradeManagement, AddExpenses,
            // AddInventoryFoundation and AddProductCatalog respectively. The
            // corresponding entity configurations point here (see the
            // "raw lower(Name) expression index" comments in
            // CatalogConfiguration / DailyEntryConfiguration /
            // ExpenseConfiguration / InventoryConfiguration).
            // No matching DROP INDEX in Down(): each index dies with the
            // table its DropTable below removes.
            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_EggGrades_AccountId_FarmId_LowerName"
                    ON "EggGrades" ("AccountId", "FarmId", lower("Name"));
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "IX_ExpenseCategories_NameCi"
                    ON "ExpenseCategories" ("AccountId", "FarmId", lower("Name"));
                """);

            migrationBuilder.Sql("""
                CREATE UNIQUE INDEX "UX_InventoryItems_Account_Farm_LowerName"
                    ON "InventoryItems" ("AccountId", "FarmId", lower("Name"));
                """);

            migrationBuilder.Sql(
                """
                CREATE UNIQUE INDEX "IX_Products_AccountId_LowerName"
                    ON "Products" ("AccountId", lower("Name"));
                """);

            // 2. Static base reference data — the default account, the four
            // assignable roles, the ten default egg grades, the six default
            // packed-unit conversions. Verbatim from #283's
            // AddBaseReferenceDataAndMustChangePassword (the schema half of
            // that migration, AspNetUsers.MustChangePassword, IS in the model
            // and is regenerated above; the data half is not and is carried
            // here).
            //
            // Still raw SQL, still NOT HasData()/InsertData(), and
            // deliberately so even though the squash removes the original
            // "must back-fill an existing install" reason:
            //   - HasData rows become part of the MODEL. Every one of these
            //     tables is user-mutable through the app (an account is
            //     renamed in Settings, `PUT /api/v1/egg-grades/{id}` renames
            //     a grade, EggUnitConversion.Update retunes EggsPerUnit), so
            //     a later model-diff would emit UpdateData/DeleteData that
            //     reverts a farm's own edits. Raw SQL seeds once and then
            //     leaves the rows alone forever.
            //   - MigrationSecurityReviewTests asserts on the SQL shape of
            //     exactly these statements (12 statements, 21 rows, every one
            //     WHERE NOT EXISTS-guarded, no credential-shaped value).
            // The WHERE NOT EXISTS guards are kept as written: on the virgin
            // database this migration now always targets they are no-ops that
            // fire every insert exactly once, and they keep the statements
            // idempotent if ever replayed by hand.
            //
            // The per-key vs whole-set gating choice of each block is
            // unchanged from #283 — grades are WHOLE-SET gated because their
            // natural key (Name) is user-renameable and a per-name guard
            // resurrects a default the farm renamed away; everything else is
            // per-key.

            // --- Default account (natural key: Id, not user-mutable). ---
            migrationBuilder.Sql(
                """
                INSERT INTO "Accounts" ("Id", "AccountId", "Name", "TimeZoneId", "Locale", "DefaultCurrencyCode", "DefaultCurrencySymbol", "DefaultCurrencyMinorUnit", "UnitSystem", "FirstDayOfWeek", "DateFormatOverride", "TimeFormatOverride", "Brand", "IsActive", "Version")
                SELECT '0000000a-0000-0000-0000-000000000001', '0000000a-0000-0000-0000-000000000001', 'Default Farm', 'UTC', 'en-US', 'USD', '$', 2, 'Metric', NULL, NULL, NULL, 'aubergine', TRUE, 0
                WHERE NOT EXISTS (SELECT 1 FROM "Accounts" WHERE "Id" = '0000000a-0000-0000-0000-000000000001');
                """);

            // --- The four assignable roles (natural key: NormalizedName —
            // RoleNameIndex, unique). Compile-time constants in
            // Domain/Accounts/Roles.cs; no role CRUD exists, so the key
            // cannot drift. ---
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

            // --- The 10 default egg grades — WHOLE-SET gated (the guard does
            // not reference "Name", so either all ten insert or none do). ---
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
            // AccountId + UnitCode, not user-mutable — UnitCode has no
            // rename path and there is no create/delete endpoint). ---
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
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Accounts");

            migrationBuilder.DropTable(
                name: "AspNetRoleClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserClaims");

            migrationBuilder.DropTable(
                name: "AspNetUserLogins");

            migrationBuilder.DropTable(
                name: "AspNetUserRoles");

            migrationBuilder.DropTable(
                name: "AspNetUserTokens");

            migrationBuilder.DropTable(
                name: "AuditEvents");

            migrationBuilder.DropTable(
                name: "BirdMovements");

            migrationBuilder.DropTable(
                name: "DailyEntryGrades");

            migrationBuilder.DropTable(
                name: "durable_jobs");

            migrationBuilder.DropTable(
                name: "EggInventoryMovements");

            migrationBuilder.DropTable(
                name: "EggUnitConversions");

            migrationBuilder.DropTable(
                name: "Expenses");

            migrationBuilder.DropTable(
                name: "FarmLogos");

            migrationBuilder.DropTable(
                name: "FeedUsages");

            migrationBuilder.DropTable(
                name: "idempotency_records");

            migrationBuilder.DropTable(
                name: "InventoryMovements");

            migrationBuilder.DropTable(
                name: "Payments");

            migrationBuilder.DropTable(
                name: "ProductEggGradeMappings");

            migrationBuilder.DropTable(
                name: "refresh_tokens");

            migrationBuilder.DropTable(
                name: "SalesOrderAllocations");

            migrationBuilder.DropTable(
                name: "simulation_seed_state");

            migrationBuilder.DropTable(
                name: "UserRoleAssignments");

            migrationBuilder.DropTable(
                name: "WaterUsages");

            migrationBuilder.DropTable(
                name: "AspNetRoles");

            migrationBuilder.DropTable(
                name: "AspNetUsers");

            migrationBuilder.DropTable(
                name: "ExpenseCategories");

            migrationBuilder.DropTable(
                name: "InventoryLots");

            migrationBuilder.DropTable(
                name: "EggLots");

            migrationBuilder.DropTable(
                name: "SalesOrderItems");

            migrationBuilder.DropTable(
                name: "DailyEntries");

            migrationBuilder.DropTable(
                name: "Flocks");

            migrationBuilder.DropTable(
                name: "InventoryItems");

            migrationBuilder.DropTable(
                name: "EggGrades");

            migrationBuilder.DropTable(
                name: "Products");

            migrationBuilder.DropTable(
                name: "SalesOrders");

            migrationBuilder.DropTable(
                name: "Customers");
        }
    }
}
