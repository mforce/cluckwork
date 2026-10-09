# #798, #799, #800: OAuth screen directions

Mockups for the OAuth milestone's screens, for the maintainer to choose from before the slices build them. Open `oauth-direction-lab.html` in a browser. It has switches for screen, direction, state, signed-in role (consent only), width (1280 or 390) and night mode. It is a prototype: English source copy, sample data, no API calls. Nothing is sent, saved or connected.

The lab uses the live app's tokens, type and controls, copied from `web/src/styles.css` and `web/src/theme/FarmThemeProvider.tsx`: the aubergine palette, Inter with Georgia titles, 4px controls, 8px panels, 12px dialogs, the lavender rail, status words with an 8px dot. Nothing new is invented. Account and Audit sit inside the maintainer's chosen C "Focus panels" compositions (`../674-tail-redesign/SELECTION.md`), and the login screens use the chosen Login B card (`AuthShell.tsx`).

## Naming

The maintainer decided the user-facing term is **Connected apps**. OAuth calls these programs "clients", and the code and issues may keep that word. Every label, heading and line of copy in the UI says "app" or "Connected apps", never "assistant" or "AI". The action is **Disconnect** everywhere, for a person's own apps and on the Owner's view. Consent still names the specific app ("Allow Claude Desktop to act as you?").

`node shoot.mjs` regenerates the 207 renders into `renders/` at 1:1 and prints each consent direction's word count. Names follow `<screen>-<direction>-<width>-<state>.png`. A `-viewport` suffix means one 1280x800 or 390x844 screen scrolled to the new part. Without it the frame is the whole page. Extra frames are `consent-<dir>-<width>-role-<Admin|Manager|ReadOnly>` (Worker is the default state) and a `-dark` frame for each screen's recommended direction. The renders are not committed; they attach to the issues, following `941-expand-chart`.

## Consent (#798)

Every direction carries what #788 requires: the app's name, the two permissions, that it acts as you and can never do more than you can, where to undo it (Account, then Connected apps), the password step, and Allow or Cancel. It also shows two safety facts: the app chose its own name and Cluckwork does not check it, and where the browser returns afterwards.

### The compact rework: D, E and F

The maintainer found A to C far too wordy (145 to 161 words before the password field), so D to F say the same things in under about 40 words. They share these parts:

- One headline question, "Let Claude Desktop act as you?", with an **Unverified app** badge beside it.
- Two permission rows with Lucide icons: "Read farm data" and "Record daily entries", each with at most one short line.
- One "acts as you" line, "Acts as you, with only your access", with a chip carrying the user's role. The chip reads "Your role: Worker" to a screen reader.
- "Returns to this computer". The address, 127.0.0.1, is under Details.
- The password field, then Allow and Cancel.
- Under the buttons, "Disconnect it anytime in Account › Connected apps." and a closed **Details** disclosure with the full explanations: the role sentence, the unverified name, the return address, what reading covers, and "Signed in as … Not you? Sign out".

Focus order is headline, permissions, password, Allow, Cancel, then Details. The headline and rows are text, so a screen reader reaches them in that order.

| | Direction | Words before the password | What it does |
|---|---|---|---|
| D | Compact card | 39 | A narrow card on the canvas. The farm and signed-in email sit above it, and the shared parts stack inside it. |
| **E** | **Login card** | **40** | The Login B card the user just signed in through. The brand panel holds Cluckwork, the farm and the signed-in email; the form side carries the request. On the phone the brand panel shrinks to one band. |
| F | Ledger | 29 | A labelled ruled list: Can, Acts as, Returns to. One value per row and no description lines, so the scope detail moves into Details. |

States for D to F: default, Details open, wrong password ("Wrong password. Nothing was connected." in a live region), asks for more (the headline becomes "Let Claude Desktop do more?", the new permission comes first with a tint and a "New" marker, and the old one drops to a muted "Already allowed" line), refused ("Nothing to approve", one line, one button), long strings, and the four roles. For Read-only, the "Record daily entries" row says "Not allowed for your role" in place of its description, because the app cannot do what the role cannot.

**Recommendation: E.** The maintainer chose Login B, so a user who has to sign in sees the request in the same card they just used, and one who is already signed in sees a card they recognise. The brand panel also answers "whose account is this?" without spending words in the form. F is the tightest; if E still reads as too much, its ledger rows could replace E's permission rows.

### A, B and C (history)

The first three directions stay in the lab, marked as history: A, the sign-in panel; B, a plain statement; and C, an "asks vs gets" table. They carry the full explanations in prose. C's per-role table is still the clearest way to show what a Read-only user's app gets, and it could return inside Details if wanted.

### Login, continuing to consent

**Chosen: B.** At 1280 the brand panel's tagline gives way to a box reading "Next: approve Claude Desktop. After you sign in, you see what it asks for and choose whether to allow it." The form is unchanged. At 390 the brand panel is a short band with no room, so the same box sits above the form instead (`login-b-390-*.png`).

A (a notice above the form) and C (a two-step header) stay in the lab for comparison.


## Connected apps on Account (#799, the user's own)

A third focus panel after Change password, titled "Connected apps". It lists name, permissions in the consent screen's words, connected date, last used, and Disconnect. States: two connected apps, the Disconnect confirmation, after disconnecting ("It stops working on its next request"), empty, and long strings.

| | Direction | What it does |
|---|---|---|
| **A** | **Ruled list** | DIRECTION.md's row rule: a text action at 1280 and a full-width 44px button at 390. |
| B | One card each | An outlined card per app with an "In use" or "Not used for 47 days" status word. |
| C | Sentence and disclosure | "2 connected apps can act as you", a nudge to disconnect the idle one, and each app as a disclosure row. |

**Recommendation: A.** It reuses the row pattern the ledgers already use and stays short when a person has one or two apps. B's idle status word is worth carrying into A's Last used column.

## The Owner's farm-wide view (#799)

The directions differ by placement, which is the open decision. Each shows every connection with person, role, app, permissions, dates and Disconnect, plus the line explaining why an Owner may do this ("does less than disabling the person").

| | Placement | Trade-off |
|---|---|---|
| A | A "Connected apps on this farm" section under the Users ledger | It sits next to the person it belongs to. The Users page gets longer, and on a phone the section is below every user. |
| B | A "Connected apps on this farm" panel in Farm settings, grouped by person | Farm settings is already Owner-only (#729). But settings are about the farm's configuration, and connections are about people. |
| **C** | **Its own Setup page, "Connected apps"** | It has room for a person filter and grows when apps do. It costs one more nav entry; on the phone it sits under More. |

**Recommendation: C.** It answers the question #799 gives this view ("is a departed employee's app still connected?") in one place, with a filter, and does not lengthen Users or Settings.

## Audit provenance (#800)

The actor stays the human (#500). The application is added beside it. Sample rows use the existing `Daily entry created` and `Daily entry draft edited` actions, which are what the phase-1 write tool produces.

| | Direction | Row | Filter |
|---|---|---|---|
| A | Tag in the summary | A "via Claude Desktop" status word at the end of the summary line | A fifth field, "Made through": Everything, The Cluckwork app, Any connected app, or one app |
| B | Who block | A plug icon in the summary; Who and Through rows in the body | A segmented Everyone, People, Connected apps control, and an App field once Connected apps is chosen |
| **C** | **Actor line on every row** | Every summary gets a second line with the actor's email, plus "via app" when one acted | A checkbox, "Only actions through connected apps", and a "Show only Claude Desktop" button inside an event |

**Recommendation: C.** Today a reader has to open each event to see who acted. C shows the actor on every row, so "Ana, or an app acting as Ana?" is answered without expanding anything, which is the gap #800 names. Its pivot button gives the per-app filter without a fifth field. A's fifth field is the fallback if the maintainer wants app filtering in the filter grid.

## Accessibility in the mockups

- Every field has a visible label tied to its input. The password errors are in `aria-live` regions with `role="alert"`, and the field gets `aria-invalid`.
- The focus ring is the app's 2px `--focus` outline. Disconnect buttons in a list carry the app's name in their accessible name ("Disconnect Claude Desktop").
- Focus order follows reading order. On consent D to F that is: headline, permissions, password, Allow, Cancel, Details. The confirmation dialog takes focus on its confirm button, matching `useConfirm`.
- Long strings add 40% to every visible string. The long app name tests wrapping in titles, table cells and dialog titles.

## Open questions for the maintainer

1. **Where does the Owner's farm-wide view go?** The recommendation is C, its own Setup page.
2. **App names are self-chosen.** Dynamic registration lets any app call itself "Claude Desktop". Every consent direction says so and shows where the answer returns to (127.0.0.1 for a desktop client). Keep this line, soften it, or add a verified-app marker later?
3. **"A person still submits them."** That is true for phase 1, where the write tool records but does not submit. The copy needs to change if a later phase adds submitting.
4. **Are connect and disconnect themselves audit events?** None of the issues say. If they are, the audit filter needs no change, but the action list gains two entries.
5. **The role label says "Admin".** The issues say "Owner"; the app shows `Admin`. The mockups follow the app.
6. **"The Cluckwork app" beside "connected apps".** Audit A's filter offers "The Cluckwork app" for changes a person made in Cluckwork itself, next to "Any connected app". Both use the word app. If that reads as ambiguous, the first option could say "People in Cluckwork".
