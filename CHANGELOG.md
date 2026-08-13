# Changelog

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
