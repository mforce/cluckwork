# Changelog

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
