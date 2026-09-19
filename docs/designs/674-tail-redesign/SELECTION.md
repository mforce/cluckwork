# Settings and support — selected compositions

Owner selections for #833, applied to both desktop and phone:

| Screen | Direction |
| --- | --- |
| Farm settings | C — Focus panels |
| Help | B — Working desk |
| Login | B — Working desk |
| Audit log | C — Focus panels |
| Export | C — Focus panels |
| Account | C — Focus panels |
| Set Password | B — Working desk |

Open `tail-direction-lab.html`. Switching screens restores that screen's selected composition; A/B/C remain available for comparison.

## Requested refinements

- Help's contents navigation remains visible while scrolling the article, with its own overflow for long contents lists. On phone it remains a horizontal strip above the article.
- Highlight the section currently being read with a tinted background, bold label and `aria-current="location"`. Update on manual scrolling as well as topic clicks, keep the active contents item visible, and clear the highlight when search returns no sections.
- Phone navigation remains five direct, equally sized items with the existing icons: Daily entry, Stock, Sales, History, More.
- Render Help copy as text with explicitly supported paired emphasis. A literal `/login?farm=<code>` is not an HTML opening tag. Rendering it as raw HTML had caused the browser to wrap the subsequent phone navigation inside `code` and break the grid.

## Login banner proposal — not an authorization change

The owner approved the farm-banner placement in Login B's left panel. The mockup reserves a labelled image slot, not an actual farm image; the empty preview retains the text fallback. This approves the visual placement, not a change to authentication or image access.

Current production banners are authenticated: `web/src/farm/useLogoObjectUrl.ts` fetches `/account/banner` through the authorized API client. Their existing use is the post-login splash. Login cannot fetch them anonymously under that contract.

Before implementing the login-banner proposal, decide how pre-login imagery is intentionally made available and how the correct farm is identified. Do not remove authorization or implicitly persist private banner bytes in the browser as part of a styling change. Keep neutral Cluckwork branding when no suitable pre-login image is available. The existing post-login splash is not removed by this proposal.

This artifact is a design prototype. It does not submit credentials, upload branding, save account data, or download real exports. Selection is not a claim that the implementation or every production state is complete.
