# Changelog

## [0.0.2](https://github.com/mforce/cluckwork/compare/v0.0.1...v0.0.2) (2026-08-03)


### Features

* **ci:** attest published image provenance and publish the digest as an asset ([#354](https://github.com/mforce/cluckwork/issues/354)) ([#384](https://github.com/mforce/cluckwork/issues/384)) ([e4b4474](https://github.com/mforce/cluckwork/commit/e4b44744651696b3c12c1884cf136d7def371c80))
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
