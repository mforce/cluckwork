# Planning records

**These are not current documentation.** Each directory is the paper trail of one
feature's design — product framing, architecture, program design, slice plan, and
the review rounds against each — captured *before and during* the work.

They describe what was **intended at the time**. Where a plan and the shipped code
disagree, the code is right and the plan is a historical record of a decision that
moved. Kept for provenance: they carry the reasoning and the rejected alternatives
that no diff shows.

| Record | Feature | Status |
|---|---|---|
| [`357-change-user-email/`](357-change-user-email/) | Owners change a user's login email (#357) | Shipped in #605; issue closed |
| [`388-worker-flock-read-scoping.md`](388-worker-flock-read-scoping.md) | Worker reads scoped to assigned flocks (#388) | Shipped in #611; issue closed |
| [`500-seeded-audit-actor/`](500-seeded-audit-actor/) | Seeded audit events carry a real actor (#500) | Shipped in #517; issue closed |
| [`508-audit-monotonic-order/`](508-audit-monotonic-order/) | Same-instant audit events order by a durable monotonic key (#508) | Shipped in #700; issue closed |
| [`562-tenant-write-token/`](562-tenant-write-token/) | `AccountId` as a concurrency token against detached cross-tenant writes (#562) | Shipped in #671; issue closed |
| [`585-587-login-credentials/`](585-587-login-credentials/) | Farm-qualified login credentials and revocable remembered farms (#585, #587) | #587 shipped in #598; #585 closed as not planned, its stable field identifiers shipped under #587 |
| [`606-step-up-flock-scope/`](606-step-up-flock-scope/) | Step-up for durable flock-scope changes (#606) | Shipped in #609; issue closed |
| [`612-worker-sale-allocation/`](612-worker-sale-allocation/) | Configurable Worker sale-allocation scope (#612) | Shipped in #619; issue closed |
| [`651-652-spa-elevation-and-caps/`](651-652-spa-elevation-and-caps/) | Elevation hierarchy and sentence-case labels (#651, #652) | Shipped in #661; issues closed |
| [`653-655-list-screens/`](653-655-list-screens/) | List-screen layout and empty states (#653, #655) | Shipped in #668, with a #653 follow-up in #678; issues closed |
| [`670-user-roles-account-id/`](670-user-roles-account-id/) | `AspNetUserRoles` carries a tenant column (#670) | Shipped in #675; issue closed |
| [`672-fake-otlp-collector/`](672-fake-otlp-collector/) | Fake OTLP collector flakes (#672, #676) | Shipped in #677; issues closed |
| [`703-shared-dialog-session/`](703-shared-dialog-session/) | Shared dialog-session guard for the SPA (#703) | Shipped in #704, #705, #706, #707, #710, #711 and #714; issue closed |
| [`721-discount-reason/`](721-discount-reason/) | Discount reason required below list price (#721) | Shipped in #756; issue closed |
| [`722-audit-price-payload/`](722-audit-price-payload/) | List, old and new price in the order-line audit payload (#722) | Shipped in #742; issue closed |
| [`723-724-discount-visibility/`](723-724-discount-visibility/) | Discount visibility on the order screen and in history (#723, #724) | Shipped in #741; issues closed |
| [`727-discount-ceiling/`](727-discount-ceiling/) | Per-farm discount ceiling with approval above it (#727) | Shipped in #766; issue closed |
| [`732-rename-account-verb/`](732-rename-account-verb/) | `rename-account` verb to change a farm code (#732) | Shipped in #733; issue closed |
| [`745-audit-details-column/`](745-audit-details-column/) | Readable audit Details column (#745) | Shipped in #749; issue closed |
| [`770-mcp-server/`](770-mcp-server/) | MCP server support — feasibility design (#770) | Design merged in #785; issue closed. Being built under epic #789: #788 shipped, #805 merged in #1181, #806 merged in #1195; the epic stays open for #807 to #811 |
| [`788-mcp-oauth/`](788-mcp-oauth/) | MCP authentication — OAuth 2.1 via OpenIddict (#788) | Shipped in slices #795 to #800, #1146, #1148 and #1164; issue closed; #793 (SPA login on OpenIddict) deferred |
| [`822-mui-revamp/`](822-mui-revamp/) | MUI revamp design doc (#822) | Design doc merged in #862; issue closed |
| [`839-integration-wall-clock/`](839-integration-wall-clock/) | Integration suite wall clock (#839) | Shipped in #861; issue closed |
| [`840-integration-flakes/`](840-integration-flakes/) | The four remaining integration flakes (#840) | Shipped in #866, #876 and #895; issue closed |
| [`863-integration-collection-split/`](863-integration-collection-split/) | Lower the integration suite floor (#863) | Shipped in #1000, #1002 and #1003; the collection split was measured and reverted in #1004; issue closed |
| [`audit-entity-history/`](audit-entity-history/) | Entity-scoped "View history" (#493) | Shipped in #516; issue closed |
| [`screenshot-pipeline-and-conventions/`](screenshot-pipeline-and-conventions/) | Screenshot pipeline and the #651/#652 conventions (#660, #662, #663, #664) | Shipped in #665; issues closed |

**Where to look instead:**

| For | Go to |
|---|---|
| The rule as it stands now | [`../../AGENTS.md`](../../AGENTS.md) |
| Why a rule exists | [`../decisions/`](../decisions/) |
| How to operate it | [`../runbooks/`](../runbooks/) |
| What the product does | [`../../specs/product/specs.md`](../../specs/product/specs.md) |

A plan is finished when its work merges. Nothing here is updated afterwards —
updating it would destroy the record of what was actually believed at the time.
The one exception is the status line at the top of each file, which matches the
table above and is kept current.
