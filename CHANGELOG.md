# Changelog

## [0.1.0](https://github.com/mforce/cluckwork/compare/v0.0.4...v0.1.0) (2026-09-06)


### ⚠ BREAKING CHANGES

* log in by farm code, with per-account email identity ([#532](https://github.com/mforce/cluckwork/issues/532)) (#564)

### Features

* **accounts:** add Account.Slug (farm code), suspend/reactivate, list-accounts verb ([#531](https://github.com/mforce/cluckwork/issues/531)) ([3fe9754](https://github.com/mforce/cluckwork/commit/3fe975454a761b2b29bc9ced67a561ccc5a5260d))
* **accounts:** provision additional farms ([#581](https://github.com/mforce/cluckwork/issues/581)) ([006f298](https://github.com/mforce/cluckwork/commit/006f298aef1ef00ff44f1b7da18f280a9ae6a67b))
* add Aspire local development AppHost ([#567](https://github.com/mforce/cluckwork/issues/567)) ([2c9e6b9](https://github.com/mforce/cluckwork/commit/2c9e6b933de5b570b7a3960277f0c911bac67bbf))
* add configurable worker sale allocation ([#619](https://github.com/mforce/cluckwork/issues/619)) ([0955095](https://github.com/mforce/cluckwork/commit/0955095f3185471a55a6890adfa827bb29dd518e))
* add searchable entity pickers ([#642](https://github.com/mforce/cluckwork/issues/642)) ([60d2053](https://github.com/mforce/cluckwork/commit/60d2053c7eb0394c04439c8eae1a3b0cfc6098a4))
* **api:** provision-account takes an optional --timezone at creation ([#603](https://github.com/mforce/cluckwork/issues/603)) ([#694](https://github.com/mforce/cluckwork/issues/694)) ([a0aee39](https://github.com/mforce/cluckwork/commit/a0aee39c51c3bf0115f581bb8dc9b658d2b40932))
* **auth:** add ApplicationUser.StepUpLogoutEpoch column ([#338](https://github.com/mforce/cluckwork/issues/338)) ([#554](https://github.com/mforce/cluckwork/issues/554)) ([18306ee](https://github.com/mforce/cluckwork/commit/18306ee3c3382eed672bfaefddb92fd007e6f0a4))
* certify over-cap simulation fixture bands ([#633](https://github.com/mforce/cluckwork/issues/633)) ([a67b2e1](https://github.com/mforce/cluckwork/commit/a67b2e1f0c3dba2a8635d76ffc8e4f6ad7e9f58b)), closes [#627](https://github.com/mforce/cluckwork/issues/627)
* **customers:** edit existing customer details ([#625](https://github.com/mforce/cluckwork/issues/625)) ([#626](https://github.com/mforce/cluckwork/issues/626)) ([062a55c](https://github.com/mforce/cluckwork/commit/062a55c88a075fc181cb675c2bd3f8d3ff856e9d))
* **jobs:** single-runner leader gate for the durable job worker ([#271](https://github.com/mforce/cluckwork/issues/271)) ([#555](https://github.com/mforce/cluckwork/issues/555)) ([4148f9b](https://github.com/mforce/cluckwork/commit/4148f9bc97d43c8e4c4811049f5b49360a1a8a63))
* let owners change user email addresses ([#605](https://github.com/mforce/cluckwork/issues/605)) ([842347b](https://github.com/mforce/cluckwork/commit/842347bd0a747b33e6b26a953cfea0733095ab61))
* log in by farm code, with per-account email identity ([#532](https://github.com/mforce/cluckwork/issues/532)) ([#564](https://github.com/mforce/cluckwork/issues/564)) ([68adb62](https://github.com/mforce/cluckwork/commit/68adb621b45567f6526b61f34c13ff728452baac))
* **ratelimit:** distributed IP-keyed auth limiters ([#544](https://github.com/mforce/cluckwork/issues/544)) ([#558](https://github.com/mforce/cluckwork/issues/558)) ([ec14972](https://github.com/mforce/cluckwork/commit/ec1497283787cd63d064d20c28227a3ea3311871))
* **ratelimit:** distributed per-account report concurrency cap with local-ceiling fallback ([#545](https://github.com/mforce/cluckwork/issues/545)) ([#559](https://github.com/mforce/cluckwork/issues/559)) ([1522e4e](https://github.com/mforce/cluckwork/commit/1522e4e99ea3e062dcb735abbdeaab3ae26190ac))
* scope Worker reads to assigned flocks ([#388](https://github.com/mforce/cluckwork/issues/388)) ([#611](https://github.com/mforce/cluckwork/issues/611)) ([5884a9a](https://github.com/mforce/cluckwork/commit/5884a9a88cbd8eeeeee5bfc111762e26f62a090a))
* shared-state ports with Redis + in-process fallback ([#543](https://github.com/mforce/cluckwork/issues/543)) ([#552](https://github.com/mforce/cluckwork/issues/552)) ([f767fa9](https://github.com/mforce/cluckwork/commit/f767fa907d4c42922c74cca59a38caba30cac442))
* suspend-account / reactivate-account operator verbs ([#534](https://github.com/mforce/cluckwork/issues/534)) ([#573](https://github.com/mforce/cluckwork/issues/573)) ([d0be26c](https://github.com/mforce/cluckwork/commit/d0be26cfa41880a2421adb780f3807ebd9edbc3e))
* **tenancy:** write-side tenant guard + single-assignment TenantContext ([#546](https://github.com/mforce/cluckwork/issues/546)) ([#561](https://github.com/mforce/cluckwork/issues/561)) ([f371f1d](https://github.com/mforce/cluckwork/commit/f371f1d0b584a66b89e86da57420b808674a9340))
* **web:** dashboard rework — capture-status tiles, 14-day trend, stock as a stacked bar ([#654](https://github.com/mforce/cluckwork/issues/654)) ([396ba23](https://github.com/mforce/cluckwork/commit/396ba233c04a84fa3452e9fd7901e4f2429d4bd7))
* **web:** date-range filters on audit and expenses, and the stock lot filter gets its bounded toolbar ([#666](https://github.com/mforce/cluckwork/issues/666), [#667](https://github.com/mforce/cluckwork/issues/667), [#653](https://github.com/mforce/cluckwork/issues/653)) ([94b188f](https://github.com/mforce/cluckwork/commit/94b188f7a95c88c5903c242c630df39a99af2090))
* **web:** elevation hierarchy and sentence-case labels ([#651](https://github.com/mforce/cluckwork/issues/651), [#652](https://github.com/mforce/cluckwork/issues/652)) ([#661](https://github.com/mforce/cluckwork/issues/661)) ([28db4c7](https://github.com/mforce/cluckwork/commit/28db4c75f7f2cd13ac00c439c87f1fa90b37d77f))
* **web:** Expenses and Audit keep a clear-filters control while rows are still showing ([#679](https://github.com/mforce/cluckwork/issues/679)) ([#697](https://github.com/mforce/cluckwork/issues/697)) ([b859982](https://github.com/mforce/cluckwork/commit/b8599822fe6fc422a56a2875b650e02f3845e7a0))
* **web:** expenses filters by a date range like its sibling screens ([#667](https://github.com/mforce/cluckwork/issues/667)) ([f13858f](https://github.com/mforce/cluckwork/commit/f13858f624d5cceb3b7eb52a666cdcd7cfe1327e))
* **web:** key the farm brand palette per farm ([#586](https://github.com/mforce/cluckwork/issues/586)) ([#600](https://github.com/mforce/cluckwork/issues/600)) ([7183a43](https://github.com/mforce/cluckwork/commit/7183a432e824f19618093ce0b6591946d1c46002))
* **web:** let operators forget remembered farms ([#598](https://github.com/mforce/cluckwork/issues/598)) ([577d94e](https://github.com/mforce/cluckwork/commit/577d94e5081716ea299896aba9749e196a1b9e8e))
* **web:** one-line provenance, bounded date filters, and empty states that invite action ([#653](https://github.com/mforce/cluckwork/issues/653), [#655](https://github.com/mforce/cluckwork/issues/655)) ([#668](https://github.com/mforce/cluckwork/issues/668)) ([80b53f4](https://github.com/mforce/cluckwork/commit/80b53f4bdf29d07652ef434852b1ac18d36208f5))
* **web:** prefill the farm code from ?farm= and remember it ([#535](https://github.com/mforce/cluckwork/issues/535)) ([#588](https://github.com/mforce/cluckwork/issues/588)) ([b7f5cc6](https://github.com/mforce/cluckwork/commit/b7f5cc6c35d3499bba91c05ea4f04e8786dc00e9))
* **web:** split authenticated routes into lazy chunks ([#620](https://github.com/mforce/cluckwork/issues/620)) ([5089271](https://github.com/mforce/cluckwork/commit/5089271f0872ec75db143eda204e738adc1a6973))
* **web:** the audit log filters by a date range, and says which window is empty ([#666](https://github.com/mforce/cluckwork/issues/666)) ([63027e0](https://github.com/mforce/cluckwork/commit/63027e01918d0ad157e5f038a50ffb06ca03bd7d))
* **web:** typeset numbers as numbers and refresh the Help glossary ([#650](https://github.com/mforce/cluckwork/issues/650), [#657](https://github.com/mforce/cluckwork/issues/657)) ([af4fe11](https://github.com/mforce/cluckwork/commit/af4fe11112c40888b1958311a131a7ded444c985))


### Bug fixes

* **api:** order same-instant audit events by a durable monotonic key ([#700](https://github.com/mforce/cluckwork/issues/700)) ([8fcf084](https://github.com/mforce/cluckwork/commit/8fcf084fb3889b78e9bee950f6e324d7e0f238dc))
* **api:** print the farm code from bootstrap-admin ([#589](https://github.com/mforce/cluckwork/issues/589)) ([#594](https://github.com/mforce/cluckwork/issues/594)) ([34032ac](https://github.com/mforce/cluckwork/commit/34032ac673a7b921d68b7904e773ad4e45cd0b4e))
* **auth:** reject invalid account claims ([#622](https://github.com/mforce/cluckwork/issues/622)) ([8d6c7fe](https://github.com/mforce/cluckwork/commit/8d6c7fe3f4e45c2d910fae5ef54fc528aab2e0f5))
* **auth:** require step-up for durable user access ([#360](https://github.com/mforce/cluckwork/issues/360)) ([#607](https://github.com/mforce/cluckwork/issues/607)) ([f767dce](https://github.com/mforce/cluckwork/commit/f767dce0073aea17a7e4e8cd644224023d325c89))
* **ci:** bound the npm audit calls and give the web job room to finish ([#686](https://github.com/mforce/cluckwork/issues/686)) ([153b7a8](https://github.com/mforce/cluckwork/commit/153b7a8e9c029ce69645a1e6bd7fbf98a548b27d))
* **ci:** escalate the audit bound to SIGKILL, so it actually bounds ([#686](https://github.com/mforce/cluckwork/issues/686)) ([a0c8f4e](https://github.com/mforce/cluckwork/commit/a0c8f4ec84de3b3d6f8719e45bf9f8e98e4965ea))
* **ci:** fail closed on invalid vulnerability config ([#621](https://github.com/mforce/cluckwork/issues/621)) ([1690db8](https://github.com/mforce/cluckwork/commit/1690db89f69982fdb1b5a7017c6a0dcdf21787c6))
* **ci:** lockfix covers the two AppHost lock files, derived from the sln ([efb05e6](https://github.com/mforce/cluckwork/commit/efb05e63163f9df0a17f5a38852827135232604f))
* **ci:** lockfix covers the two AppHost lock files, derived from the sln ([8986d77](https://github.com/mforce/cluckwork/commit/8986d77b5b6d7ae56073fca6e095c798df21658c))
* **ci:** remove invalid XML comment from nuget.lockfix.config ([#541](https://github.com/mforce/cluckwork/issues/541)) ([5f1bc0a](https://github.com/mforce/cluckwork/commit/5f1bc0a8d7594e440120b52c408014674a707d55))
* **ci:** the advisory vuln gate no longer blocks on an unusable report ([#686](https://github.com/mforce/cluckwork/issues/686)) ([aaf6934](https://github.com/mforce/cluckwork/commit/aaf693449bd58b6290ec747cb491c1698f96c7a6))
* **ci:** the advisory vuln gate no longer blocks on an unusable report ([#686](https://github.com/mforce/cluckwork/issues/686)) ([64f1f53](https://github.com/mforce/cluckwork/commit/64f1f53ed04be37e57a1541f52c103772dd5ed90))
* **i18n:** tl help text names the saleable flag and unit-system setting what their labels call them ([#688](https://github.com/mforce/cluckwork/issues/688)) ([#696](https://github.com/mforce/cluckwork/issues/696)) ([bfd24d7](https://github.com/mforce/cluckwork/commit/bfd24d7eb50e93767d667aa4e502885a904bcf9f))
* **infra:** AccountId must be a non-nullable Guid or both tenant write layers refuse ([#673](https://github.com/mforce/cluckwork/issues/673)) ([#695](https://github.com/mforce/cluckwork/issues/695)) ([2470c4e](https://github.com/mforce/cluckwork/commit/2470c4e85929e1eea6466217ef8b15f3f52eed54))
* require step-up for flock scope changes ([#609](https://github.com/mforce/cluckwork/issues/609)) ([4151f89](https://github.com/mforce/cluckwork/commit/4151f89f1ca7b2350acfe54672ff5903545e240e))
* scope legacy logout to selected farm ([#624](https://github.com/mforce/cluckwork/issues/624)) ([fae8d82](https://github.com/mforce/cluckwork/commit/fae8d82175fb53fa95610a85a7c87779dbd17b42))
* **seed:** drain the daily-entry lock sweep so deep simulation fixtures validate ([#644](https://github.com/mforce/cluckwork/issues/644)) ([730fa23](https://github.com/mforce/cluckwork/commit/730fa238749ef4b816923de64fb7b28c20ad560e)), closes [#638](https://github.com/mforce/cluckwork/issues/638)
* **tenancy:** AccountId is a concurrency token, so the database refuses a detached cross-tenant write ([#562](https://github.com/mforce/cluckwork/issues/562)) ([4d1dfa3](https://github.com/mforce/cluckwork/commit/4d1dfa3729a8d1feea80d1261a27ad1373068e65))
* **tenancy:** AspNetUserRoles carries a tenant column, so a role write naming another farm's user is refused ([#670](https://github.com/mforce/cluckwork/issues/670)) ([fc0552a](https://github.com/mforce/cluckwork/commit/fc0552aef110973be3cee8b369d4c9862045db90))
* **tests:** bump the image-pin allow-list counts for the AppHost LocalPorts tests ([#593](https://github.com/mforce/cluckwork/issues/593)) ([58d3056](https://github.com/mforce/cluckwork/commit/58d30568414f3f6b8f1f5742f005a1b3b63c4420))
* **tests:** the OTLP collector survives a lost port race and ignores traffic that is not an export ([#672](https://github.com/mforce/cluckwork/issues/672), [#676](https://github.com/mforce/cluckwork/issues/676)) ([#677](https://github.com/mforce/cluckwork/issues/677)) ([965c737](https://github.com/mforce/cluckwork/commit/965c73745fc2784bb155cfdf8ff399870ed0c196))
* **web:** a scoped audit view filtered to nothing names both the record and the range ([#666](https://github.com/mforce/cluckwork/issues/666)) ([41bbfe1](https://github.com/mforce/cluckwork/commit/41bbfe12aab8efac6be4aa79e2e8fd74275dfbaf))
* **web:** an abandoned order attempt's success no longer hijacks the dialog that replaced it ([#702](https://github.com/mforce/cluckwork/issues/702)) ([522c699](https://github.com/mforce/cluckwork/commit/522c699e6e424055c93f69b468c9bff00722f680))
* **web:** capture screens open on the flock you last used, and assigning one no longer guesses ([#646](https://github.com/mforce/cluckwork/issues/646)) ([#699](https://github.com/mforce/cluckwork/issues/699)) ([7f8f317](https://github.com/mforce/cluckwork/commit/7f8f31725608e42ac236a52b3bbbcf4cb9b187fc))
* **web:** date validation gets one boundary table instead of one case per review round ([#666](https://github.com/mforce/cluckwork/issues/666)) ([215f830](https://github.com/mforce/cluckwork/commit/215f830a37509b81497dfc09fa7cced0c51a2015))
* **web:** keep a paged window and an item panel on the user's newest intent ([#645](https://github.com/mforce/cluckwork/issues/645)) ([d81bccf](https://github.com/mforce/cluckwork/commit/d81bccf7c42525735c16076ff768d2ca1c54fc11))
* **web:** make login take the cross-tab cookie lock so a racing refresh cannot restore the wrong session ([#648](https://github.com/mforce/cluckwork/issues/648)) ([ff18beb](https://github.com/mforce/cluckwork/commit/ff18beb9d1cd7dc5f931b992313eb7841f0bf660))
* **web:** page truncated customer and movement tables with usePagedList ([7cfe4d6](https://github.com/mforce/cluckwork/commit/7cfe4d6fdd82ac0caf0a70572eab1c5a229ae9e7))
* **web:** the audit date filter accepts low-numbered years, and its empty state covers every narrowing ([#666](https://github.com/mforce/cluckwork/issues/666)) ([af52d25](https://github.com/mforce/cluckwork/commit/af52d2562efdda36e393e8887fcda7b3130779f5))
* **web:** the audit date filter rejects impossible dates, and its history guard actually guards ([#666](https://github.com/mforce/cluckwork/issues/666)) ([8d51846](https://github.com/mforce/cluckwork/commit/8d518461d5ad90088124b4656cdb20903ab4c094))
* **web:** the expense range bounds are not capped at today, which the month-end default exceeds ([#667](https://github.com/mforce/cluckwork/issues/667)) ([7e01864](https://github.com/mforce/cluckwork/commit/7e01864849c4f530cdb77362905880f572740811))
* **web:** the help text calls the expiry field what the field calls itself ([#666](https://github.com/mforce/cluckwork/issues/666)) ([2fd1f3c](https://github.com/mforce/cluckwork/commit/2fd1f3c4db50a813f1fa4bcf11b592c669c2c517))
* **web:** the stock lot date range sits in the bounded toolbar ([#653](https://github.com/mforce/cluckwork/issues/653)) ([43dec5e](https://github.com/mforce/cluckwork/commit/43dec5e160c2c86eaabe1afde77e787219e9da60))


### Refactoring

* **web:** extract SalesPage's dialog-write wrapper into a shared useDialogAction hook ([#703](https://github.com/mforce/cluckwork/issues/703)) ([#704](https://github.com/mforce/cluckwork/issues/704)) ([60ee9d9](https://github.com/mforce/cluckwork/commit/60ee9d9a5226b77d23ce7b20d101748c013db53d))


### Documentation

* add k6 preparation steps to the dev-database fixture runbook ([#643](https://github.com/mforce/cluckwork/issues/643)) ([a4f1f09](https://github.com/mforce/cluckwork/commit/a4f1f0944100898d8808c3508d8415b454e39713))
* add runbook for loading the simulation fixture into a dev database ([#639](https://github.com/mforce/cluckwork/issues/639)) ([2d143b8](https://github.com/mforce/cluckwork/commit/2d143b817ae9940f5a17e18af7826076dd56387c))
* **agents:** find guards by grepping registry readers; amend issues a PR overtakes ([#580](https://github.com/mforce/cluckwork/issues/580)) ([fe3fde8](https://github.com/mforce/cluckwork/commit/fe3fde8e15a383659c86682c8b7a3887318a2038))
* **aspire:** record the second local database and pin the AppHost dashboard ports ([#623](https://github.com/mforce/cluckwork/issues/623)) ([713b941](https://github.com/mforce/cluckwork/commit/713b9411f55c0f14f67d020365e8651e0ced075d))
* compress AGENTS.md to one paragraph per rule, and draw the two orders that matter ([#551](https://github.com/mforce/cluckwork/issues/551)) ([997ae8a](https://github.com/mforce/cluckwork/commit/997ae8aea5f360aff9cf884270a3eebf27ad1f27))
* item 7 names each screen's actual initial filter value ([#666](https://github.com/mforce/cluckwork/issues/666)) ([70a53d8](https://github.com/mforce/cluckwork/commit/70a53d8f3a93c7506d292cd6f43b0cb103b489f6))
* multi-farm tenancy decision record and AGENTS/GLOSSARY sync ([#537](https://github.com/mforce/cluckwork/issues/537)) ([#601](https://github.com/mforce/cluckwork/issues/601)) ([2c34771](https://github.com/mforce/cluckwork/commit/2c34771342cac2df26654ac90b2680567f2ffb1b))
* name the scoped filtered-empty key and state the [#653](https://github.com/mforce/cluckwork/issues/653) relationship plainly ([#666](https://github.com/mforce/cluckwork/issues/666)) ([0e93dac](https://github.com/mforce/cluckwork/commit/0e93dac6c70d305a8842ce54c8c4f24356baf2b3))
* note that a PackageReference in Directory.Build.props is invisible to the dependency graph ([4845724](https://github.com/mforce/cluckwork/commit/48457247e4bb1fdb829a3055ba678627a582cf40))
* record [#579](https://github.com/mforce/cluckwork/issues/579) as won't-fix — suspension is immediate for use, not issuance ([#582](https://github.com/mforce/cluckwork/issues/582)) ([7a3be40](https://github.com/mforce/cluckwork/commit/7a3be4098d673658954736195342e052ca1c0c5f))
* record the [#508](https://github.com/mforce/cluckwork/issues/508) audit ordering key and the tracked-file guard lesson ([#701](https://github.com/mforce/cluckwork/issues/701)) ([08964e9](https://github.com/mforce/cluckwork/commit/08964e98371b6467ebc6381207dd9932ebddc82e))
* screenshots of the running SPA in the README ([#550](https://github.com/mforce/cluckwork/issues/550)) ([711488a](https://github.com/mforce/cluckwork/commit/711488a9b32cf59eeacd4e5bdd5aa6586c3f8b50))
* **sim:** commit the dashboard screenshot, capture the palette matrix, and record the [#651](https://github.com/mforce/cluckwork/issues/651)/[#652](https://github.com/mforce/cluckwork/issues/652) conventions ([#660](https://github.com/mforce/cluckwork/issues/660), [#662](https://github.com/mforce/cluckwork/issues/662), [#663](https://github.com/mforce/cluckwork/issues/663), [#664](https://github.com/mforce/cluckwork/issues/664)) ([#665](https://github.com/mforce/cluckwork/issues/665)) ([930ea30](https://github.com/mforce/cluckwork/commit/930ea3085187e19c53e3782b011a15fee8ce5784))
* specify searchable entity picker ([#641](https://github.com/mforce/cluckwork/issues/641)) ([91d4300](https://github.com/mforce/cluckwork/commit/91d43009c45be0ed28ea86447cb8fd6cdbf53c0f))
* split the README into audience-scoped docs and adopt repo-template scaffolding ([#548](https://github.com/mforce/cluckwork/issues/548)) ([b3f3fcf](https://github.com/mforce/cluckwork/commit/b3f3fcf8b7f62289e01132eec9418eaf4dda7e6f))
* surface Aspire local development workflow ([#568](https://github.com/mforce/cluckwork/issues/568)) ([a343baa](https://github.com/mforce/cluckwork/commit/a343baa6cafb7ffc71de0475ae9bab8e6e6c9dfb))
* **web:** the date-cap help text covers every stocked item, not only feed ([#666](https://github.com/mforce/cluckwork/issues/666), [#667](https://github.com/mforce/cluckwork/issues/667)) ([c8433c5](https://github.com/mforce/cluckwork/commit/c8433c56321419ee60bc9e99a2777e6860ebefaa))
* **web:** the help text claims only what is true of recording, and says nothing about filter caps ([#666](https://github.com/mforce/cluckwork/issues/666), [#667](https://github.com/mforce/cluckwork/issues/667)) ([e2f63d1](https://github.com/mforce/cluckwork/commit/e2f63d16f77cfca7b91f53aa062c2d811e867c1a))
* **web:** the help text describes the date-range filters that shipped ([#666](https://github.com/mforce/cluckwork/issues/666), [#667](https://github.com/mforce/cluckwork/issues/667)) ([c3275b7](https://github.com/mforce/cluckwork/commit/c3275b7b939daf7fc40db3c30f690e997198087d))
* **web:** the help text stops describing a cap the filters no longer have ([#666](https://github.com/mforce/cluckwork/issues/666), [#667](https://github.com/mforce/cluckwork/issues/667)) ([49654cd](https://github.com/mforce/cluckwork/commit/49654cd0bf8ebedde6b34026bd2bece83812ff1f))

## [0.0.4](https://github.com/mforce/cluckwork/compare/v0.0.3...v0.0.4) (2026-08-13)


### Features

* **api,web:** add an independent farm banner shown on a post-login splash ([#496](https://github.com/mforce/cluckwork/issues/496)) ([1732a38](https://github.com/mforce/cluckwork/commit/1732a380733059748e64a360b60e96c5affb33c4))
* **api,web:** show who created and last changed a record, inline on its own page ([#494](https://github.com/mforce/cluckwork/issues/494)) ([#503](https://github.com/mforce/cluckwork/issues/503)) ([4ffa7f1](https://github.com/mforce/cluckwork/commit/4ffa7f17f3fad71ab562380186f210b997d49791))
* **api:** promote/demote a user's role ([#475](https://github.com/mforce/cluckwork/issues/475)) ([f273879](https://github.com/mforce/cluckwork/commit/f273879c470647fc142f0d4afa06acd14497ed89))
* **stock:** let an admin write off lost egg stock without restating production ([#464](https://github.com/mforce/cluckwork/issues/464)) ([79a4e94](https://github.com/mforce/cluckwork/commit/79a4e94370bed8b99e34e0a142db172fa03b73de))
* **stock:** page and date-filter the lot drill-down so older lots stay reachable ([#465](https://github.com/mforce/cluckwork/issues/465)) ([#467](https://github.com/mforce/cluckwork/issues/467)) ([1b6c86c](https://github.com/mforce/cluckwork/commit/1b6c86c7389595131e3e52e11bd4d547b5e1143e))
* **users:** disable and re-enable a user without deleting them ([#356](https://github.com/mforce/cluckwork/issues/356)) ([#492](https://github.com/mforce/cluckwork/issues/492)) ([3f5c370](https://github.com/mforce/cluckwork/commit/3f5c370fb5de4fcc3868c0ecc268ddd553319277))
* **web,api:** entity-scoped audit history reachable from any record ([#493](https://github.com/mforce/cluckwork/issues/493)) ([#516](https://github.com/mforce/cluckwork/issues/516)) ([c14d5b6](https://github.com/mforce/cluckwork/commit/c14d5b6cb274859ccd6ecca6ef09428fdaf2ad45))
* **web:** a per-place error store, with Sales as its first consumer ([#479](https://github.com/mforce/cluckwork/issues/479)) ([#489](https://github.com/mforce/cluckwork/issues/489)) ([4f493ec](https://github.com/mforce/cluckwork/commit/4f493ecfccfbd4d58a37e509b13f20c87f39af82))
* **web:** cascading record-type filter on the Audit page ([#521](https://github.com/mforce/cluckwork/issues/521)) ([64bab23](https://github.com/mforce/cluckwork/commit/64bab234ee5d0cbd4b3c1592d3ff5fee51bdb393))
* **web:** display app version in the sidebar, sourced from version.txt ([#459](https://github.com/mforce/cluckwork/issues/459)) ([abdcb98](https://github.com/mforce/cluckwork/commit/abdcb98007d8fcde4823f4b0f376103ec11e04cf))
* **web:** offer common date/time format presets, with a custom fallback ([#463](https://github.com/mforce/cluckwork/issues/463)) ([7cb01b4](https://github.com/mforce/cluckwork/commit/7cb01b4da1f7e41877e0f9acbae5af619e19f0f4))


### Bug fixes

* **api:** give every boot guard an explicit process role, and fail closed on unusable JWT keys ([#507](https://github.com/mforce/cluckwork/issues/507)) ([925c31c](https://github.com/mforce/cluckwork/commit/925c31c38cd7e221fd8ad4ac0d772839ada241a7))
* **api:** seeded demo and simulation records name a real person ([#517](https://github.com/mforce/cluckwork/issues/517)) ([a552d08](https://github.com/mforce/cluckwork/commit/a552d0814cddd83b2f5b5c1f53baaca734fb8191))
* **auth:** measure the refresh grace window from the read, not the request start ([#471](https://github.com/mforce/cluckwork/issues/471)) ([2612e06](https://github.com/mforce/cluckwork/commit/2612e06f4bc34c4a666cf9334285e6ece13feb06))
* **web:** announce the update and farm warnings a dialog made inert ([#499](https://github.com/mforce/cluckwork/issues/499)) ([2ea2226](https://github.com/mforce/cluckwork/commit/2ea22266c911432ec7e4fb3308c1d045f17eb884))
* **web:** give every dialog screen its own error slot ([#479](https://github.com/mforce/cluckwork/issues/479)) ([#491](https://github.com/mforce/cluckwork/issues/491)) ([64a3780](https://github.com/mforce/cluckwork/commit/64a378076cb794ff1ee95f534916d2c07224e5d8))
* **web:** give the sales dialogs their own error slot ([#477](https://github.com/mforce/cluckwork/issues/477)) ([#478](https://github.com/mforce/cluckwork/issues/478)) ([d157a97](https://github.com/mforce/cluckwork/commit/d157a97b9a2f9a26c01bda1fd43af3c89144ecd9))
* **web:** one page, one scroll lock and one live dialog ([#482](https://github.com/mforce/cluckwork/issues/482)) ([#483](https://github.com/mforce/cluckwork/issues/483)) ([4340f54](https://github.com/mforce/cluckwork/commit/4340f542b4d52fd68bd4f53715c73ec9d60582e8))
* **web:** put the release-please version marker on the value line ([#524](https://github.com/mforce/cluckwork/issues/524)) ([6e67668](https://github.com/mforce/cluckwork/commit/6e676682d8d3ab93d1b000053f6a4ee6081b4d55)), closes [#458](https://github.com/mforce/cluckwork/issues/458)
* **web:** render a sales mutation error inside the dialog that raised it ([#474](https://github.com/mforce/cluckwork/issues/474)) ([#476](https://github.com/mforce/cluckwork/issues/476)) ([22cf6dc](https://github.com/mforce/cluckwork/commit/22cf6dcfc66d1a057eef80cf3ec06e5d04191abc))
* **web:** render a wide farm logo at its natural aspect in the sidebar ([#498](https://github.com/mforce/cluckwork/issues/498)) ([0dd5ea1](https://github.com/mforce/cluckwork/commit/0dd5ea1ccfc5fa358465a2822f8a56ab091777bd))
* **web:** tag the sales dialog error by scope instead of assuming one dialog ([#480](https://github.com/mforce/cluckwork/issues/480)) ([#481](https://github.com/mforce/cluckwork/issues/481)) ([eb6d80f](https://github.com/mforce/cluckwork/commit/eb6d80f85b508b2c8a37f6fe13ec28b7127cf5e4))


### Refactoring

* **web:** one paged-list discipline for every filtered screen ([#469](https://github.com/mforce/cluckwork/issues/469)) ([#473](https://github.com/mforce/cluckwork/issues/473)) ([9f74b4b](https://github.com/mforce/cluckwork/commit/9f74b4b38d9f8276be623f3ea2f23f56fa908851))


### Documentation

* **agents:** note graphify update cadence is periodic, not per-edit ([#497](https://github.com/mforce/cluckwork/issues/497)) ([fb53d1f](https://github.com/mforce/cluckwork/commit/fb53d1ff05f074997d75dffb3d3bd39291042c73))
* **agents:** update phase context now that epic [#14](https://github.com/mforce/cluckwork/issues/14) is closed ([#515](https://github.com/mforce/cluckwork/issues/515)) ([851033b](https://github.com/mforce/cluckwork/commit/851033b2e986b95aed8bfeb580c9446130b639e6))
* pin the one-serving-instance deploy invariant ([#271](https://github.com/mforce/cluckwork/issues/271)) ([#484](https://github.com/mforce/cluckwork/issues/484)) ([61f26a7](https://github.com/mforce/cluckwork/commit/61f26a74be7bdddaabb91549e2f11ca62bed9603))
* **web:** widen the Help line [#478](https://github.com/mforce/cluckwork/issues/478) narrowed, now that it is true ([#479](https://github.com/mforce/cluckwork/issues/479)) ([#495](https://github.com/mforce/cluckwork/issues/495)) ([9dcd233](https://github.com/mforce/cluckwork/commit/9dcd2338edf59ca561e17cd62ee3ba33e077dd95))

## [0.0.3](https://github.com/mforce/cluckwork/compare/v0.0.2...v0.0.3) (2026-08-08)


### Features

* **api:** a two-stage Serilog pipeline so every sink can be wrapped ([#426](https://github.com/mforce/cluckwork/issues/426)) ([aa3619d](https://github.com/mforce/cluckwork/commit/aa3619d3a594f899d54c45dee4552823b709a7fc))
* **api:** purge aged idempotency_records on the durable-job worker ([#259](https://github.com/mforce/cluckwork/issues/259)) ([#422](https://github.com/mforce/cluckwork/issues/422)) ([0966f11](https://github.com/mforce/cluckwork/commit/0966f11282cdebd46d3376983c9823ffe3437852))
* **api:** redact sensitive log content and emit stable security events ([#273](https://github.com/mforce/cluckwork/issues/273)) ([#349](https://github.com/mforce/cluckwork/issues/349)) ([455dd70](https://github.com/mforce/cluckwork/commit/455dd7031dbf86c9f6321b40f17915cfa9a8909f))
* **ci:** add a workflow_dispatch job for the k6 load-test baseline ([#432](https://github.com/mforce/cluckwork/issues/432)) ([72c6fda](https://github.com/mforce/cluckwork/commit/72c6fda211e31ad77ff5b3c22ab46a497adef84a))


### Bug fixes

* **api:** arm the farm-logo upload cap before IdempotencyMiddleware buffers the body ([#448](https://github.com/mforce/cluckwork/issues/448)) ([44c633e](https://github.com/mforce/cluckwork/commit/44c633e1a9a1772a48ed73187748d925d98c7499))
* **api:** prove queue entry for both racers in the currency-lock FIFO test ([#402](https://github.com/mforce/cluckwork/issues/402)) ([#425](https://github.com/mforce/cluckwork/issues/425)) ([085a907](https://github.com/mforce/cluckwork/commit/085a907740c2b7dc454b5a7bb61320a97b9aecb5))
* **ci:** stop the release changelog silently losing entries ([#411](https://github.com/mforce/cluckwork/issues/411)) ([a29b13f](https://github.com/mforce/cluckwork/commit/a29b13ff7178ac0c77a0669920a8e0d2b2a39691))
* **cli:** recover-admin no longer migrates, closing the DDL-privilege gap ([#453](https://github.com/mforce/cluckwork/issues/453)) ([2283d71](https://github.com/mforce/cluckwork/commit/2283d712df5d25c6dbde0c8b9b24e60b1c5de6b9))
* **e2e:** assert the [#433](https://github.com/mforce/cluckwork/issues/433) post-race session contract and run the quick smoke suite on PRs ([#455](https://github.com/mforce/cluckwork/issues/455)) ([#456](https://github.com/mforce/cluckwork/issues/456)) ([607da04](https://github.com/mforce/cluckwork/commit/607da044921302a2fbf8dfb38194a40af1676214))
* **e2e:** race waitForRequest with goto() to stop session-races flake ([#429](https://github.com/mforce/cluckwork/issues/429)) ([24e0859](https://github.com/mforce/cluckwork/commit/24e0859ac2caece4c92c62c1b954af12b5a1f1bd)), closes [#428](https://github.com/mforce/cluckwork/issues/428)
* **spa:** announce the UsersPage load-failure to screen readers ([#419](https://github.com/mforce/cluckwork/issues/419)) ([0e4d6bf](https://github.com/mforce/cluckwork/commit/0e4d6bf6a2f9b1548809f1830ad02ea5f01179b5))
* **web:** always revoke a stale flight's cookie, not just when logged out ([#393](https://github.com/mforce/cluckwork/issues/393)) ([#433](https://github.com/mforce/cluckwork/issues/433)) ([99d62a1](https://github.com/mforce/cluckwork/commit/99d62a17071bcc7a214a07dc765b5d95e12a3153))
* **web:** stop mobile grid tracks from blowing out the layout viewport ([#441](https://github.com/mforce/cluckwork/issues/441)) ([#447](https://github.com/mforce/cluckwork/issues/447)) ([013bb07](https://github.com/mforce/cluckwork/commit/013bb077d1a80d6a12efbfe17ccece8a1ffd44ac))


### Documentation

* **agents:** add a Communicating section on response style ([#420](https://github.com/mforce/cluckwork/issues/420)) ([86335eb](https://github.com/mforce/cluckwork/commit/86335eb4eb3325569671573ee64173dec1b587c7))
* **agents:** record the guard-writing rules [#407](https://github.com/mforce/cluckwork/issues/407) paid five rounds for ([#412](https://github.com/mforce/cluckwork/issues/412)) ([08d41f6](https://github.com/mforce/cluckwork/commit/08d41f63b0024b0b4a9142c9b858e3926bd56c50))
* **agents:** relocate AGENTS.md rationale to docs/decisions, compress to ~4.3k words ([#416](https://github.com/mforce/cluckwork/issues/416)) ([b734f59](https://github.com/mforce/cluckwork/commit/b734f5918632e68ad84986545a3863a0dd7b60fe))
* **readme:** document bootstrap-admin for a production host ([#418](https://github.com/mforce/cluckwork/issues/418)) ([72ef8db](https://github.com/mforce/cluckwork/commit/72ef8db5e7f0442b28983413dbb535d62b05cf8f))

## [0.0.2](https://github.com/mforce/cluckwork/compare/v0.0.1...v0.0.2) (2026-08-03)


### Features

* **auth:** credential epoch: per-request revocation checks ([#399](https://github.com/mforce/cluckwork/issues/399)) ([b39e8fb](https://github.com/mforce/cluckwork/commit/b39e8fb963174eb84760e394378763ac2b804bf6))
* **ci:** attest published image provenance and publish the digest as an asset ([#354](https://github.com/mforce/cluckwork/issues/354)) ([#384](https://github.com/mforce/cluckwork/issues/384)) ([e4b4474](https://github.com/mforce/cluckwork/commit/e4b44744651696b3c12c1884cf136d7def371c80))
* **eggs:** make cracked and dirty eggs sellable stock via condition grades ([#396](https://github.com/mforce/cluckwork/issues/396)) ([#407](https://github.com/mforce/cluckwork/issues/407)) ([ef9a64b](https://github.com/mforce/cluckwork/commit/ef9a64ba77067375d3bb0b029e11347cdc7c7521))
* **history:** adjust modal mirrors the daily-entry two-step form ([#403](https://github.com/mforce/cluckwork/issues/403)) ([25e5969](https://github.com/mforce/cluckwork/commit/25e596993b412c4b346cd35f93cf0419e5863618))
* **obs:** emit compact JSON logs to stdout in Production ([#405](https://github.com/mforce/cluckwork/issues/405)) ([82f2e77](https://github.com/mforce/cluckwork/commit/82f2e77f484c02a0b7f28e2213f5f6e236e5187a))
* **sim:** canary-under-load UX probe recording Core Web Vitals alongside the k6 baseline ([#391](https://github.com/mforce/cluckwork/issues/391)) ([b114511](https://github.com/mforce/cluckwork/commit/b11451163988f78a8521c0763434b37ddd1c2f5d))


### Bug fixes

* **ci:** give the promote job a repo for gh to act on ([#351](https://github.com/mforce/cluckwork/issues/351)) ([#378](https://github.com/mforce/cluckwork/issues/378)) ([e747dfe](https://github.com/mforce/cluckwork/commit/e747dfe0c4ed68973e9d5b21f5f238de4791e01a))
* **daily-entry:** require exact grade reconciliation on submit and adjust ([#400](https://github.com/mforce/cluckwork/issues/400)) ([519f045](https://github.com/mforce/cluckwork/commit/519f045a2b52e2e5ec10f80fe924a40f84e4ffe3))
* **deploy:** pin Traefik by digest and bump v3.5 to v3.7.10 ([#369](https://github.com/mforce/cluckwork/issues/369)) ([#383](https://github.com/mforce/cluckwork/issues/383)) ([5b7c3f5](https://github.com/mforce/cluckwork/commit/5b7c3f5caa318f9f5e6565c4cd6fd10f484ec1ac))
* **sales:** reject fractional order-line quantities instead of leaking a JSON-binding error ([#401](https://github.com/mforce/cluckwork/issues/401)) ([862b79f](https://github.com/mforce/cluckwork/commit/862b79fee644eca0818e07ee49ad4d7fe2863023))
* **sim:** unbreak the [#243](https://github.com/mforce/cluckwork/issues/243) harness against main, with a local self-check ([#370](https://github.com/mforce/cluckwork/issues/370)) ([#371](https://github.com/mforce/cluckwork/issues/371)) ([fa84b05](https://github.com/mforce/cluckwork/commit/fa84b055a8ac34c5d579be9125e8eedeac646e33))


### Documentation

* **agents:** require write-contract changes to update the non-CI writers ([#395](https://github.com/mforce/cluckwork/issues/395)) ([3586ebc](https://github.com/mforce/cluckwork/commit/3586ebc706503994f14d749c76662fc111e28e69))

## 0.0.1 (2026-08-02)


### Features

* **auth:** tell the operator when an instance has no admin yet ([#363](https://github.com/mforce/cluckwork/issues/363)) ([be8517f](https://github.com/mforce/cluckwork/commit/be8517f38845f04555cf33dfaf813370965a0c35))
* **ci:** version releases through a release PR and promote images by digest ([#351](https://github.com/mforce/cluckwork/issues/351)) ([#362](https://github.com/mforce/cluckwork/issues/362)) ([8990fec](https://github.com/mforce/cluckwork/commit/8990fec775436f21d65706f8124fca3b9baaa933))


### Bug fixes

* **ci:** open the release PR with a downscoped GitHub App token ([#351](https://github.com/mforce/cluckwork/issues/351)) ([#367](https://github.com/mforce/cluckwork/issues/367)) ([3407288](https://github.com/mforce/cluckwork/commit/3407288dba493e644683596122ca669ea3a26bd4))
