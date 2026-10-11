# Changelog

## [1.1.0](https://github.com/mforce/cluckwork/compare/v1.0.0...v1.1.0) (2026-10-11)


### Features

* add the connected-app consent screen with step-up, and run the OAuth server in Production ([#1144](https://github.com/mforce/cluckwork/issues/1144)) ([e40924b](https://github.com/mforce/cluckwork/commit/e40924b8978c00dc9a409c4918acc4c5d618fa60))
* **api:** let OAuth clients register themselves, and sweep expired OAuth rows ([#1137](https://github.com/mforce/cluckwork/issues/1137)) ([bd454b3](https://github.com/mforce/cluckwork/commit/bd454b335b41cf9a99dd9a41004fbbf8133d733c))
* **api:** persist the Data Protection key ring in Postgres ([#1125](https://github.com/mforce/cluckwork/issues/1125)) ([e033d26](https://github.com/mforce/cluckwork/commit/e033d26aa8c9a2017eeff0fb265c4d4474b84f0c))
* **api:** run every fail-closed check on OAuth-authenticated requests ([#1136](https://github.com/mforce/cluckwork/issues/1136)) ([4521319](https://github.com/mforce/cluckwork/commit/45213192c24d31bb6d11e714e3fce495660e63c9))
* **api:** stand up OpenIddict as an OAuth 2.1 authorization server ([#1135](https://github.com/mforce/cluckwork/issues/1135)) ([4961d00](https://github.com/mforce/cluckwork/commit/4961d0038e40f39a722a288899170f26e58d988c))
* let an Owner turn connected apps off for the farm ([#1147](https://github.com/mforce/cluckwork/issues/1147)) ([077f27e](https://github.com/mforce/cluckwork/commit/077f27e76a35e2ab64eede203679b0e042aa007b))
* let users and Owners see and disconnect connected apps ([#1145](https://github.com/mforce/cluckwork/issues/1145)) ([a6729e4](https://github.com/mforce/cluckwork/commit/a6729e48922a9e4b6822a6496172ddb0015a0e25))
* **mcp:** add McpCallContext, the identity bridge for MCP tools ([#1181](https://github.com/mforce/cluckwork/issues/1181)) ([3134800](https://github.com/mforce/cluckwork/commit/31348001d12898017757e0b6fa055eff946ce94a))
* **mcp:** map /mcp for connected apps, with role and scope gates on tools ([#1195](https://github.com/mforce/cluckwork/issues/1195)) ([6502603](https://github.com/mforce/cluckwork/commit/6502603eb8070a480af9c4c119dc07e11cc866c7))
* **oauth:** accept client ID metadata documents beside registration ([#1152](https://github.com/mforce/cluckwork/issues/1152)) ([bd4e5ec](https://github.com/mforce/cluckwork/commit/bd4e5eca22e5ebdf8953e64bd75d0706a704ad73))
* **oauth:** log rate-limit rejections on the OAuth endpoints as security events ([#1174](https://github.com/mforce/cluckwork/issues/1174)) ([9f7efd4](https://github.com/mforce/cluckwork/commit/9f7efd4002d41277f7c886e8b8fb973d457baeb4))
* record and show which connected app acted in the audit log ([#1138](https://github.com/mforce/cluckwork/issues/1138)) ([9934ef7](https://github.com/mforce/cluckwork/commit/9934ef77c32c8398abaab2e6fa0347ed804db531))


### Bug fixes

* **apphost:** pass the real API address as the OAuth issuer, and add the OAuth runbook ([#1163](https://github.com/mforce/cluckwork/issues/1163)) ([a06f7ff](https://github.com/mforce/cluckwork/commit/a06f7ff6d17165804cad367c9ba5a9d6f9981da3))
* **sales:** refresh the order's list row after a line changes ([#1191](https://github.com/mforce/cluckwork/issues/1191)) ([91b8324](https://github.com/mforce/cluckwork/commit/91b8324e78cf6472b31f5f4cd4b7692dd3675497))
* **sales:** scale a line's default and list price to its unit ([#1166](https://github.com/mforce/cluckwork/issues/1166)) ([5c2b0c0](https://github.com/mforce/cluckwork/commit/5c2b0c0c5e56ea6a6bae33acf022346d0c07fc25))
* **test:** remove unused usings that break main's build ([#1206](https://github.com/mforce/cluckwork/issues/1206)) ([9272270](https://github.com/mforce/cluckwork/commit/92722706909358496db25476b0802d5737b6a809))
* **web:** bump brace-expansion override to ^5.0.12 ([#1151](https://github.com/mforce/cluckwork/issues/1151)) ([e4ec534](https://github.com/mforce/cluckwork/commit/e4ec53479236d86fa37e98ef743bdf0b41ede81d))
* **web:** fill the expanded Lay rate chart and allow ranges up to a year ([#1162](https://github.com/mforce/cluckwork/issues/1162)) ([3e879b0](https://github.com/mforce/cluckwork/commit/3e879b0c6a4062a511a859279e2c670733c766c6))


### Refactoring

* **web:** split SalesPage into a sales feature folder ([#1159](https://github.com/mforce/cluckwork/issues/1159)) ([25d4a58](https://github.com/mforce/cluckwork/commit/25d4a58806145a1b0e256cc46f972bfa21fc87e2))


### Documentation

* **plans:** mark each planning record with its shipped status ([#1198](https://github.com/mforce/cluckwork/issues/1198)) ([b6e5103](https://github.com/mforce/cluckwork/commit/b6e5103475e8ed97da00764482423d4b0187fdd1))

## [1.0.0](https://github.com/mforce/cluckwork/compare/v0.1.5...v1.0.0) (2026-10-08)


### CI

* hotfix release lines and minor-per-release versioning ([#1130](https://github.com/mforce/cluckwork/issues/1130)) ([ea52751](https://github.com/mforce/cluckwork/commit/ea527511407cd1d2635ada6085db0d3af8b2afe6))

## [0.1.5](https://github.com/mforce/cluckwork/compare/v0.1.4...v0.1.5) (2026-10-07)


### Features

* **arch:** add CW1004 at Info with shared contract and adapter-root definitions ([#1117](https://github.com/mforce/cluckwork/issues/1117)) ([baa5c4d](https://github.com/mforce/cluckwork/commit/baa5c4d5e8236d89c536a7f674c0f7c2ddb22001))
* **arch:** make CW1004 a build error ([#1123](https://github.com/mforce/cluckwork/issues/1123)) ([8cb14e6](https://github.com/mforce/cluckwork/commit/8cb14e6741a3def0d1135012bcd62b20a385ac6c))
* **ci:** publish amd64 and arm64 images ([#996](https://github.com/mforce/cluckwork/issues/996)) ([ce4a4ab](https://github.com/mforce/cluckwork/commit/ce4a4ab6845c987dbeafe9152fee55a122400d1a))


### Bug fixes

* **auth:** refuse a stored credential epoch below 1 ([#1036](https://github.com/mforce/cluckwork/issues/1036)) ([02e8a63](https://github.com/mforce/cluckwork/commit/02e8a6333bbc18cf80ba20e4e64dae39dea52ab9))
* **e2e:** select a farm-local expense range across month boundaries ([#1011](https://github.com/mforce/cluckwork/issues/1011)) ([944acb0](https://github.com/mforce/cluckwork/commit/944acb0e3fa2a2b030ad494c0a8e874f7a8a8384))
* **inventory:** see a flock archived while feed usage waits on the item lock ([#1026](https://github.com/mforce/cluckwork/issues/1026)) ([582f8c8](https://github.com/mforce/cluckwork/commit/582f8c8a91ed750d6d4c982ab16d26e304d70e91))
* **seed:** let the demo seed retry after a late failure ([#1076](https://github.com/mforce/cluckwork/issues/1076)) ([5897e34](https://github.com/mforce/cluckwork/commit/5897e341492d6d024fdb319d0c4f38f49401def8))
* **seed:** never let the demo seed cleanup delete the Owner's data ([#1083](https://github.com/mforce/cluckwork/issues/1083)) ([d525a0e](https://github.com/mforce/cluckwork/commit/d525a0ee3bb25fbe683e51ac34e26fad68607056))
* **test:** repair demo seed false pass and measure collection floor ([#1000](https://github.com/mforce/cluckwork/issues/1000)) ([af3eb47](https://github.com/mforce/cluckwork/commit/af3eb471b336071ccea904f6e2d8d6b3f48eeaf4))


### Performance

* **seed:** release tracked entities between days in DemoDataSeeder ([#1044](https://github.com/mforce/cluckwork/issues/1044)) ([5d0a565](https://github.com/mforce/cluckwork/commit/5d0a565dadfd768e9df6f3564e868afe89653bb4))
* **seed:** release tracked entities between days in SimulationDataSeeder ([#1049](https://github.com/mforce/cluckwork/issues/1049)) ([9194b21](https://github.com/mforce/cluckwork/commit/9194b21071e256f1fb4b593417b0b169a38f7eb2))
* **test:** reuse one Postgres server for integration databases ([#1002](https://github.com/mforce/cluckwork/issues/1002)) ([1953cde](https://github.com/mforce/cluckwork/commit/1953cdea0737a6bb382d051680bcbac9bc3af639))
* **test:** serialize the five full-seed integration classes ([#1003](https://github.com/mforce/cluckwork/issues/1003)) ([ad258d8](https://github.com/mforce/cluckwork/commit/ad258d8f21a517bd32ecd214526318f511234abd))


### Refactoring

* **access:** centralize live reads and isolate lifecycle ([#857](https://github.com/mforce/cluckwork/issues/857) E1) ([#1055](https://github.com/mforce/cluckwork/issues/1055)) ([d908f2f](https://github.com/mforce/cluckwork/commit/d908f2fd6063056bdd7e9e9b2d979e4a91cef509))
* **access:** declare fixture contracts and preserve actors ([#857](https://github.com/mforce/cluckwork/issues/857) E2) ([#1060](https://github.com/mforce/cluckwork/issues/1060)) ([cd1de3a](https://github.com/mforce/cluckwork/commit/cd1de3a864fe0bc475f0098cc4005783cffd2e5c))
* **access:** name flock assignments through IFlockLookup, removing the last compatibility exception ([#1070](https://github.com/mforce/cluckwork/issues/1070)) ([4e9bd1c](https://github.com/mforce/cluckwork/commit/4e9bd1ca3ead8502de59926aae5131d356b25545))
* **access:** put the operator verbs behind IAccessOperations and the purge behind IRefreshTokenPurge ([#1047](https://github.com/mforce/cluckwork/issues/1047)) ([7014566](https://github.com/mforce/cluckwork/commit/7014566cab13bf7ee5163ab2c9b98055e36ecc75))
* **accounts:** move the cross-farm account reads behind IFarmDirectory ([#1043](https://github.com/mforce/cluckwork/issues/1043)) ([b998b07](https://github.com/mforce/cluckwork/commit/b998b0720633f50a3687ed2a416ec607e4cc33d8))
* **api:** register each module from its own file ([#1065](https://github.com/mforce/cluckwork/issues/1065)) ([36a87a7](https://github.com/mforce/cluckwork/commit/36a87a7faf3ae7098e1f3126461647b693d2d5b8))
* **arch:** close the Platform composition slice ([#1066](https://github.com/mforce/cluckwork/issues/1066)) ([e63ad81](https://github.com/mforce/cluckwork/commit/e63ad81d8307ee328108c61f3adcc26f0828001f))
* **arch:** finish package-by-module — contracts by folder, no marks or claim lists ([#1099](https://github.com/mforce/cluckwork/issues/1099)) ([23bb192](https://github.com/mforce/cluckwork/commit/23bb192125f79824d1936782ec841a054e15ce14))
* **arch:** give adapters Farm's farm-code rule and image-size errors through its contract ([#1121](https://github.com/mforce/cluckwork/issues/1121)) ([cce672b](https://github.com/mforce/cluckwork/commit/cce672ba2d72cd14aec278f26eed43ad576b6c3d))
* **arch:** move Access into Modules/Access ([#1095](https://github.com/mforce/cluckwork/issues/1095)) ([ccb5558](https://github.com/mforce/cluckwork/commit/ccb5558400415a7c39f5739cf042f195d997c007))
* **arch:** move Commerce into Modules/Commerce ([#1094](https://github.com/mforce/cluckwork/issues/1094)) ([deae524](https://github.com/mforce/cluckwork/commit/deae5245aa1e9936b605753d1a84bd175fe3d5dd))
* **arch:** move Egg Operations into Modules/EggOperations ([#1093](https://github.com/mforce/cluckwork/issues/1093)) ([51e9cbb](https://github.com/mforce/cluckwork/commit/51e9cbb544065d53a97c298d694f2aebcc5bbc68))
* **arch:** move Farm into Modules/Farm ([#1096](https://github.com/mforce/cluckwork/issues/1096)) ([57cdbc6](https://github.com/mforce/cluckwork/commit/57cdbc6be0282896b9c90f4ae108a6825c10dd1a))
* **arch:** move Finance into Modules/Finance ([#1090](https://github.com/mforce/cluckwork/issues/1090)) ([5422898](https://github.com/mforce/cluckwork/commit/5422898e79033164de5996347e0a39ecf44d9a49))
* **arch:** move Flock Management into Modules/FlockManagement ([#1091](https://github.com/mforce/cluckwork/issues/1091)) ([acc8f73](https://github.com/mforce/cluckwork/commit/acc8f732c1cbc26a35dc45fa5ef1842ce8ab24cd))
* **arch:** move General Inventory into Modules/GeneralInventory ([#1092](https://github.com/mforce/cluckwork/issues/1092)) ([541eca9](https://github.com/mforce/cluckwork/commit/541eca9876cb8a71611ab17f5f2b1e1bab618073))
* **arch:** move Insights into Modules/Insights ([#1089](https://github.com/mforce/cluckwork/issues/1089)) ([d4175b5](https://github.com/mforce/cluckwork/commit/d4175b5fccedcc0a9f7acaa151ffb34ab61f84cd))
* **arch:** move repositories and EF configurations into their modules ([#1097](https://github.com/mforce/cluckwork/issues/1097)) ([fb3a9cd](https://github.com/mforce/cluckwork/commit/fb3a9cd31e4b8b38e65de575356b84ef761cbcde))
* **arch:** move the 7 login records into Access's public contract ([#1104](https://github.com/mforce/cluckwork/issues/1104)) ([91263f4](https://github.com/mforce/cluckwork/commit/91263f4c642eaf0136bde763ee5c57e02043c7c7))
* **arch:** name flocks through Flock Management's contract and publish Access's credential rules ([#1119](https://github.com/mforce/cluckwork/issues/1119)) ([0a11748](https://github.com/mforce/cluckwork/commit/0a117481f7bf17c469e874aa2c95fde6e134a330))
* **arch:** publish DiscountCeiling in Commerce's contract ([#1118](https://github.com/mforce/cluckwork/issues/1118)) ([2cf8deb](https://github.com/mforce/cluckwork/commit/2cf8deb54baaea7b0646a056cab10c24f085007a))
* **arch:** publish Farm's shared value types in its contract ([#1120](https://github.com/mforce/cluckwork/issues/1120)) ([9eb2ba8](https://github.com/mforce/cluckwork/commit/9eb2ba85b20fd4f953ae83d768d1d20480bcfccf))
* **arch:** put module rules beside the code they describe ([#1086](https://github.com/mforce/cluckwork/issues/1086)) ([02c299a](https://github.com/mforce/cluckwork/commit/02c299a7293120e74ee01faf1e2b49fbd02c75bf))
* **arch:** remove slop from the package-by-module and CW1004 work ([#1128](https://github.com/mforce/cluckwork/issues/1128)) ([30b0e11](https://github.com/mforce/cluckwork/commit/30b0e1120a658c19bfce82530afcfd9cce8f0257))
* **arch:** stage a new farm's defaults through Egg Operations' and Commerce's provisioning ports ([#1122](https://github.com/mforce/cluckwork/issues/1122)) ([c74cd01](https://github.com/mforce/cluckwork/commit/c74cd016af27bc7013ba38862c38b1542e4b1379))
* **auth:** move the credential-epoch check behind ICredentialEpochVerifier ([#1032](https://github.com/mforce/cluckwork/issues/1032)) ([3b89567](https://github.com/mforce/cluckwork/commit/3b8956791b8bbe7da27a0229e10b4a8aefd430eb))
* **auth:** route the auth endpoints through IAccessModule ([#1041](https://github.com/mforce/cluckwork/issues/1041)) ([a78729d](https://github.com/mforce/cluckwork/commit/a78729d83c49efa229ce7f9b3ec2e037b9f32654))
* **eggs:** put egg operations behind an IEggOperationsModule contract ([#1028](https://github.com/mforce/cluckwork/issues/1028)) ([d6dbd47](https://github.com/mforce/cluckwork/commit/d6dbd47da165ac4e12d6cf96a2751423535138a7))
* **farm:** put farm settings behind an IFarmModule contract ([#1015](https://github.com/mforce/cluckwork/issues/1015)) ([4c429f0](https://github.com/mforce/cluckwork/commit/4c429f0864f14ddbc6e7e4e82d1553f175eaa341))
* **finance:** put Finance behind an IFinanceModule contract ([#1010](https://github.com/mforce/cluckwork/issues/1010)) ([b9f7f76](https://github.com/mforce/cluckwork/commit/b9f7f76b42ce71b0149fdcbdcf495f3bdfd6dc10))
* **flocks:** put flock lifecycle behind an IFlockModule contract ([#1021](https://github.com/mforce/cluckwork/issues/1021)) ([08cf44c](https://github.com/mforce/cluckwork/commit/08cf44c6669d8740ba5c53cf354850db1c725449))
* **insights:** read reports, exports and audit provenance through an Insights facade ([#1012](https://github.com/mforce/cluckwork/issues/1012)) ([7ce95cf](https://github.com/mforce/cluckwork/commit/7ce95cf475318e161c39647529e5b5f17a41b24a))
* **inventory:** put general inventory behind an IInventoryModule contract ([#1027](https://github.com/mforce/cluckwork/issues/1027)) ([54a5f29](https://github.com/mforce/cluckwork/commit/54a5f298cba500f743af0e278df7ea46ed9baf1c))
* **persistence:** split business record census by module ([#970](https://github.com/mforce/cluckwork/issues/970)) ([f28bb47](https://github.com/mforce/cluckwork/commit/f28bb47098a43dbaa634e3292b5c11bf8c9c893e))
* **repositories:** delete the generic repository base and its unused members ([#1042](https://github.com/mforce/cluckwork/issues/1042)) ([ed0c1bb](https://github.com/mforce/cluckwork/commit/ed0c1bbaa1fa15092fe821becf3753a823075cf0))
* **sales:** put Commerce behind a contract and reach egg stock through IEggStock ([#1030](https://github.com/mforce/cluckwork/issues/1030)) ([7bb45cc](https://github.com/mforce/cluckwork/commit/7bb45cc3d2159821e71e7d1bd54c94e18d740acf))
* **seed:** read and purge demo fixtures through module ports ([#1064](https://github.com/mforce/cluckwork/issues/1064)) ([5c239ee](https://github.com/mforce/cluckwork/commit/5c239ee0d7f8c96e75f2d80d7713268bc5d9bb91))
* **seed:** read and write simulation fixtures through module ports ([#1063](https://github.com/mforce/cluckwork/issues/1063)) ([d4dda8e](https://github.com/mforce/cluckwork/commit/d4dda8e859b092c600c405cf37c34f81951fc680))
* **seed:** read Finance and General Inventory fixtures through module ports ([#1062](https://github.com/mforce/cluckwork/issues/1062)) ([a4783c9](https://github.com/mforce/cluckwork/commit/a4783c9c2cb8da4ac4de8ba4fc9256be70d6733b))
* **test:** give the architecture and tenant guards a loader seam ([#1067](https://github.com/mforce/cluckwork/issues/1067)) ([9477029](https://github.com/mforce/cluckwork/commit/94770298fb74f170aac23f76e006818953297fd3))
* **test:** move the module ledger rows into typed C# ([#1069](https://github.com/mforce/cluckwork/issues/1069)) ([f6e2d04](https://github.com/mforce/cluckwork/commit/f6e2d047be78e8d712275b9367f4b955efeb44cf))
* **test:** move the tenant-bypass registries into C# ([#1068](https://github.com/mforce/cluckwork/issues/1068)) ([bfe8211](https://github.com/mforce/cluckwork/commit/bfe82116c5a72691d45710414fd0c4b2e70fbaf9))
* **users:** put user administration behind IAccessModule and peer reads behind IAccessLookup ([#1037](https://github.com/mforce/cluckwork/issues/1037)) ([75e2b1b](https://github.com/mforce/cluckwork/commit/75e2b1be08585b091611e393fb97ba9a904374f7))


### Documentation

* **arch:** describe Domain contracts and CW1004 in the code-layers table ([#1124](https://github.com/mforce/cluckwork/issues/1124)) ([8ab37fe](https://github.com/mforce/cluckwork/commit/8ab37fe705f57c4912f969a83c943e4d96acd0af))
* **arch:** draw the deployment, code layers and feature modules ([#1098](https://github.com/mforce/cluckwork/issues/1098)) ([7e9e440](https://github.com/mforce/cluckwork/commit/7e9e440c5602656508be7961d8e92b9f12e16589))
* **arch:** tighten the architecture diagram prose and guard ([#1126](https://github.com/mforce/cluckwork/issues/1126)) ([53b3e3b](https://github.com/mforce/cluckwork/commit/53b3e3b2b55c12ef5b568cf14689e28abae4dc37))
* **ci:** measure the integration collection split (no CI win; reverted) ([#1004](https://github.com/mforce/cluckwork/issues/1004)) ([6166890](https://github.com/mforce/cluckwork/commit/61668907a97e4abfdaa05e73a6eb743005939406))
* deslop the README and remove its em-dashes ([#994](https://github.com/mforce/cluckwork/issues/994)) ([d32638e](https://github.com/mforce/cluckwork/commit/d32638edc23f826be95a2215bf115bca15d0bad0))
* show the egg loop as a GIF in the README ([#992](https://github.com/mforce/cluckwork/issues/992)) ([c8b382a](https://github.com/mforce/cluckwork/commit/c8b382a1a13873f55acc299badbf8ff5729bb3a9))
* split AGENTS.md into directory-scoped rule files ([#1035](https://github.com/mforce/cluckwork/issues/1035)) ([07fc42c](https://github.com/mforce/cluckwork/commit/07fc42cdeed34b5fbf825c0ab74b0e538c3b9b7a))

## [0.1.4](https://github.com/mforce/cluckwork/compare/v0.1.3...v0.1.4) (2026-09-28)


### Features

* **web:** compact Sales and Feed & inventory lists on phones ([#988](https://github.com/mforce/cluckwork/issues/988)) ([5d55cec](https://github.com/mforce/cluckwork/commit/5d55cec2b371c388a194714da83dd5771caf69fe))
* **web:** compact two-line History rows on phones ([#983](https://github.com/mforce/cluckwork/issues/983)) ([2bcbbd5](https://github.com/mforce/cluckwork/commit/2bcbbd5d14b101961c2dfeff2f303e6bba65f426))
* **web:** compact Water, Feed and Expenses on phones ([#986](https://github.com/mforce/cluckwork/issues/986)) ([25cc7fa](https://github.com/mforce/cluckwork/commit/25cc7fa1f81da02e255138fff8abf5e62691bbae))
* **web:** live-preview the farm palette before saving ([#979](https://github.com/mforce/cluckwork/issues/979)) ([a5f159c](https://github.com/mforce/cluckwork/commit/a5f159c1b1b12f6d75d47fd6b12f02c6713fb0cc))


### Bug fixes

* **web:** close setup inspectors, prioritize Dashboard, and align shell colors ([#975](https://github.com/mforce/cluckwork/issues/975)) ([fcb43ee](https://github.com/mforce/cluckwork/commit/fcb43ee45bfe876bf3b9955cf9a6baa93aeb742b))
* **web:** keep focused fields readable in dark mode ([#990](https://github.com/mforce/cluckwork/issues/990)) ([f4c2b47](https://github.com/mforce/cluckwork/commit/f4c2b47793845fe9336e9603c1b6cacc0d7cd549))
* **web:** keep setup-list rows one line tall on phones ([#984](https://github.com/mforce/cluckwork/issues/984)) ([eebbdd8](https://github.com/mforce/cluckwork/commit/eebbdd84575ea805fab31bcea7219ed7cf3a81b8))
* **web:** keep the PWA theme-color in step with the app theme ([#976](https://github.com/mforce/cluckwork/issues/976)) ([e19d6f8](https://github.com/mforce/cluckwork/commit/e19d6f88f228e9faedd917efa2515e8cf5be890d))

## [0.1.3](https://github.com/mforce/cluckwork/compare/v0.1.2...v0.1.3) (2026-09-26)


### Features

* **dashboard:** redesign the Operations Desk and show order grade names ([#912](https://github.com/mforce/cluckwork/issues/912)) ([49fc2a3](https://github.com/mforce/cluckwork/commit/49fc2a3356d9ee3b296e6a7157fdc1aca7ba5765))
* **eggs:** warn per grade when available stock falls below its floor ([#950](https://github.com/mforce/cluckwork/issues/950)) ([8125845](https://github.com/mforce/cluckwork/commit/8125845a7067469cb066b5f6977592c3f311d205))
* redesign daily entry as count workbench ([#913](https://github.com/mforce/cluckwork/issues/913)) ([aa685ad](https://github.com/mforce/cluckwork/commit/aa685ad225e7cce8cd0f40e69179e4b80e97e278))
* **web:** add a flock scope selector to the Dashboard, fixing the strip's scale fallback ([#918](https://github.com/mforce/cluckwork/issues/918)) ([f4fa057](https://github.com/mforce/cluckwork/commit/f4fa0570028545ed5855eb9502579f53d59b77ed))
* **web:** add a Lay rate range switcher and page the Dashboard panels ([#940](https://github.com/mforce/cluckwork/issues/940)) ([e0c1f68](https://github.com/mforce/cluckwork/commit/e0c1f686ccc7c9214ee0ac191a263308e4fcdf8f))
* **web:** convert and redesign Settings, Help, Login, Audit, Export, Account and Set Password ([#833](https://github.com/mforce/cluckwork/issues/833)) ([#901](https://github.com/mforce/cluckwork/issues/901)) ([4ee8b03](https://github.com/mforce/cluckwork/commit/4ee8b03699492bee3730321d366a19c728430152))
* **web:** convert Dialog and useConfirm to MUI Dialog ([#892](https://github.com/mforce/cluckwork/issues/892)) ([647ed64](https://github.com/mforce/cluckwork/commit/647ed64fd75377de2aa5840725e0a830bb978d60))
* **web:** convert the CRUD lists to MUI ([#897](https://github.com/mforce/cluckwork/issues/897)) ([009db86](https://github.com/mforce/cluckwork/commit/009db86d677336f8b16ddfeb1e50fe750cea87cd))
* **web:** expand the Lay rate chart into an overview map and a scrolling daily window ([#958](https://github.com/mforce/cluckwork/issues/958)) ([852c845](https://github.com/mforce/cluckwork/commit/852c84598bf75fcbb8cddb936f49bb535061bc5b))
* **web:** give display figures an optical size with Inter's opsz axis ([#948](https://github.com/mforce/cluckwork/issues/948)) ([a8de94e](https://github.com/mforce/cluckwork/commit/a8de94e4879804f17f52ee3b09d86fdb7f28c411))
* **web:** migrate small controls to MUI ([#934](https://github.com/mforce/cluckwork/issues/934)) ([e684a3e](https://github.com/mforce/cluckwork/commit/e684a3ef3e9255d34e9ba17f5112315269ff4758))
* **web:** redesign Sales with a MUI order desk and settlement rail ([#927](https://github.com/mforce/cluckwork/issues/927)) ([99e7e83](https://github.com/mforce/cluckwork/commit/99e7e83af78a26d57f8a27c758bb034b2e165376))
* **web:** redesign seven ledgers as a Field Console with MUI ([#899](https://github.com/mforce/cluckwork/issues/899)) ([167ac68](https://github.com/mforce/cluckwork/commit/167ac68ecbe5761c47eefc7e0e93578e2b914f81))
* **web:** redesign the pending-update prompt as a branded overlay ([#938](https://github.com/mforce/cluckwork/issues/938)) ([aaf171d](https://github.com/mforce/cluckwork/commit/aaf171d26245dba86a3419688e03a9f27fad52ab))
* **web:** redesign the setup lists as a table with a bottom inspector ([#939](https://github.com/mforce/cluckwork/issues/939)) ([0c0faad](https://github.com/mforce/cluckwork/commit/0c0faadad1a7b3819adba215c9773215779dd7af))
* **web:** replace NamedEntityPicker's combobox with MUI Autocomplete ([#898](https://github.com/mforce/cluckwork/issues/898)) ([0f7b966](https://github.com/mforce/cluckwork/commit/0f7b966636570763528d6b84dbd7bd3efce39491))


### Bug fixes

* filter Audit log by record type ([#965](https://github.com/mforce/cluckwork/issues/965)) ([da203b7](https://github.com/mforce/cluckwork/commit/da203b72d8d17000d5f205bfd8a338a979e42080))
* **seed:** date the demo farm's draft by the farm clock, not the UTC date ([#893](https://github.com/mforce/cluckwork/issues/893)) ([ef0f2a9](https://github.com/mforce/cluckwork/commit/ef0f2a9029f023637acb46398695fa872b8b4d23))
* **seed:** give House 2 realistic demo history ([#917](https://github.com/mforce/cluckwork/issues/917)) ([1472221](https://github.com/mforce/cluckwork/commit/1472221a6a490d9d7b8ef6cb901b739027437ec1))
* **seed:** rate the fixture farms at 86–94% hen-day and flag a rate over 100% ([#959](https://github.com/mforce/cluckwork/issues/959)) ([a426efe](https://github.com/mforce/cluckwork/commit/a426efedff073a45f1231d05cf8445d8a92c4c0a))
* **web:** fit Users actions and compact selected-record inspectors ([#947](https://github.com/mforce/cluckwork/issues/947)) ([3e80bc6](https://github.com/mforce/cluckwork/commit/3e80bc67c06d1f9a3e75e711caa4df1c9f04eba2))
* **web:** keep the Lay rate readout inside its reserved row ([#964](https://github.com/mforce/cluckwork/issues/964)) ([1598f9b](https://github.com/mforce/cluckwork/commit/1598f9b30cd5c5e6305a66339a1a23fa57073239))
* **web:** make FieldConsole row links theme-aware in dark mode ([#931](https://github.com/mforce/cluckwork/issues/931)) ([0f0afcd](https://github.com/mforce/cluckwork/commit/0f0afcd04bd13bcdcaf640e62de8a09d5fbaafff))
* **web:** restore the grouped, alphabetised Help glossary ([#657](https://github.com/mforce/cluckwork/issues/657)) ([#924](https://github.com/mforce/cluckwork/issues/924)) ([dcd0e1b](https://github.com/mforce/cluckwork/commit/dcd0e1b2a355d124367795a2bd7ec68c49211390)), closes [#833](https://github.com/mforce/cluckwork/issues/833)
* **web:** stop the customer picker reserving 240px of height inside dialogs, and keep the phone dialog footer side by side ([#896](https://github.com/mforce/cluckwork/issues/896)) ([91e3d65](https://github.com/mforce/cluckwork/commit/91e3d65253e0abc6c8719599a56ecbdf9a8a7cde))
* **web:** stop the Lay rate flock picker dialog from shrink-wrapping ([#937](https://github.com/mforce/cluckwork/issues/937)) ([190dcd6](https://github.com/mforce/cluckwork/commit/190dcd67e1728e897a747a12b280f972189bd18f))


### Documentation

* correct four false claims about CI coverage and gh pr edit ([#956](https://github.com/mforce/cluckwork/issues/956)) ([7d73bfe](https://github.com/mforce/cluckwork/commit/7d73bfe977bc0c0f007a1e4d6b1ad26ea11901d4))
* deslop AGENTS.md without weakening its rules ([#921](https://github.com/mforce/cluckwork/issues/921)) ([#925](https://github.com/mforce/cluckwork/issues/925)) ([e44d435](https://github.com/mforce/cluckwork/commit/e44d435ab5b0342c857dc5e2ea38c84ba6803e76))
* document local JWT dev-keypair setup in CONTRIBUTING ([#949](https://github.com/mforce/cluckwork/issues/949)) ([d4f3585](https://github.com/mforce/cluckwork/commit/d4f3585dc487aa587aa303f92eaf292678f95804))
* recapture the README screenshots after the SPA revamp ([#961](https://github.com/mforce/cluckwork/issues/961)) ([cb37bf0](https://github.com/mforce/cluckwork/commit/cb37bf02c6b0d96a912477c31de5607094029ae8))
* **web:** tighten the Help page and product glossary ([#957](https://github.com/mforce/cluckwork/issues/957)) ([bd267ba](https://github.com/mforce/cluckwork/commit/bd267bad911721585145bbd43a513a4a5afd08b5))

## [0.1.2](https://github.com/mforce/cluckwork/compare/v0.1.1...v0.1.2) (2026-09-16)


### Features

* **data:** standardize business record chronology ([#820](https://github.com/mforce/cluckwork/issues/820)) ([6231b31](https://github.com/mforce/cluckwork/commit/6231b316ec6d3af4d5aa9ae61ad5072ae3a011bd))
* **infra:** optional leader-lease endpoint for pooled deploys ([#869](https://github.com/mforce/cluckwork/issues/869)) ([e9bc6a7](https://github.com/mforce/cluckwork/commit/e9bc6a783138236e2d16e5065cda169a5b5e94c0))
* **sim:** seed a second farm for the README dashboard capture ([#867](https://github.com/mforce/cluckwork/issues/867)) ([de407c6](https://github.com/mforce/cluckwork/commit/de407c623601d8419e6b0152f77c539e0a63ad40))
* **web:** adopt MUI, themed from the farm palette tokens ([#674](https://github.com/mforce/cluckwork/issues/674)) ([#860](https://github.com/mforce/cluckwork/issues/860)) ([6c83c5c](https://github.com/mforce/cluckwork/commit/6c83c5cbff8bc49942965beb9dca8fae2bb0507f))
* **web:** convert Daily entry to MUI, field-first on the phone ([#888](https://github.com/mforce/cluckwork/issues/888)) ([b66f8b8](https://github.com/mforce/cluckwork/commit/b66f8b8602b122dec3dc11244660fd648653241b))
* **web:** convert the Dashboard and app shell to MUI ([#829](https://github.com/mforce/cluckwork/issues/829)) ([#883](https://github.com/mforce/cluckwork/issues/883)) ([2e94277](https://github.com/mforce/cluckwork/commit/2e94277bb29b3d120c806d0bdd0eb4cc4a8c5114))
* **web:** retire the Slack-blue link colour for ink + a rule underline ([#884](https://github.com/mforce/cluckwork/issues/884)) ([c08f9d8](https://github.com/mforce/cluckwork/commit/c08f9d86d33b8f72002da3f17332f28b2546501b))
* **web:** serve a per-request CSP nonce so Emotion's styles apply under style-src 'self' ([#874](https://github.com/mforce/cluckwork/issues/874)) ([ba4e6f3](https://github.com/mforce/cluckwork/commit/ba4e6f3c0274bff53beea5658290bab21999dc8f))
* **web:** visual language theme overrides for the MUI revamp ([#864](https://github.com/mforce/cluckwork/issues/864)) ([#882](https://github.com/mforce/cluckwork/issues/882)) ([0bb6b73](https://github.com/mforce/cluckwork/commit/0bb6b735e6016ed96c476eabda25d4c9d0296eb5))
* **web:** whole-app MUI baseline, theme policy guard and the [#740](https://github.com/mforce/cluckwork/issues/740) phone action rule ([#823](https://github.com/mforce/cluckwork/issues/823)) ([#871](https://github.com/mforce/cluckwork/issues/871)) ([af565e4](https://github.com/mforce/cluckwork/commit/af565e4709a5584e247225c29921ff54b40f3612))


### Bug fixes

* **auth:** fail closed on unresolved flock-scope actors ([#787](https://github.com/mforce/cluckwork/issues/787)) ([#868](https://github.com/mforce/cluckwork/issues/868)) ([16d0350](https://github.com/mforce/cluckwork/commit/16d03505c3be7109f5a9927c40fa6aedb47c8339))
* **auth:** make farm configuration owner-only ([#870](https://github.com/mforce/cluckwork/issues/870)) ([42f9036](https://github.com/mforce/cluckwork/commit/42f903695a65421137b0443a9a93dbc087ea8fde))
* **e2e:** repoint the canary at the markup two PRs replaced ([#844](https://github.com/mforce/cluckwork/issues/844)) ([18b45dc](https://github.com/mforce/cluckwork/commit/18b45dc0a708ac95fb8a597984908e584c0cff65))
* **i18n:** tl glossary uses the standard passive of ilagay ([#813](https://github.com/mforce/cluckwork/issues/813)) ([20dec10](https://github.com/mforce/cluckwork/commit/20dec10049cf59ae4e1413ba557d022b5ff4a270)), closes [#738](https://github.com/mforce/cluckwork/issues/738)
* **sim:** stop the k6-baseline EXIT trap masking a clean run as failed ([#838](https://github.com/mforce/cluckwork/issues/838)) ([f5ec96f](https://github.com/mforce/cluckwork/commit/f5ec96f4f9a31bb22c87c661a2c44182a6c2cbf6))
* **web:** declare the rule tokens the Dashboard reads, and guard undeclared custom properties ([#885](https://github.com/mforce/cluckwork/issues/885)) ([5bead1f](https://github.com/mforce/cluckwork/commit/5bead1fc505a6749509fd8ff60f785e44d5612a6))


### Performance

* **ci:** start the serialized integration collection first ([#861](https://github.com/mforce/cluckwork/issues/861)) ([1dcc7f6](https://github.com/mforce/cluckwork/commit/1dcc7f60d1ed14b313f82beafcb60b1da6527b4e)), closes [#839](https://github.com/mforce/cluckwork/issues/839)


### Documentation

* **auth:** record the OAuth 2.1 decision for MCP authentication ([#801](https://github.com/mforce/cluckwork/issues/801)) ([0510854](https://github.com/mforce/cluckwork/commit/0510854397745568201512920e1cbd9ac56ca057))
* **designs:** MUI revamp design doc, component map, layout system, IA ([#862](https://github.com/mforce/cluckwork/issues/862)) ([da49481](https://github.com/mforce/cluckwork/commit/da49481c3684a785e995fb01f30073123bbab348))
* **readme:** recapture the daily entry, reports and sales screenshots ([#865](https://github.com/mforce/cluckwork/issues/865)) ([f18e336](https://github.com/mforce/cluckwork/commit/f18e336d34d6ce6caf5f58efcf9dd586605c0469))
* **specs:** correct the sales_order_items column list in §10.5 ([#812](https://github.com/mforce/cluckwork/issues/812)) ([afe4a02](https://github.com/mforce/cluckwork/commit/afe4a02d36280692d3d3a3e98280a0c354ff92c3)), closes [#737](https://github.com/mforce/cluckwork/issues/737)

## [0.1.1](https://github.com/mforce/cluckwork/compare/v0.1.0...v0.1.1) (2026-09-12)


### Bug fixes

* **dashboard:** give recent sales real columns and make both charts readable ([#781](https://github.com/mforce/cluckwork/issues/781)) ([7193ebe](https://github.com/mforce/cluckwork/commit/7193ebe2a1cf918c1d1366a4a22721c65300ebf4))
* **dashboard:** tell a day with no entry from a day that laid no eggs ([#791](https://github.com/mforce/cluckwork/issues/791)) ([48c10e5](https://github.com/mforce/cluckwork/commit/48c10e563712c73283e105ad33621f60cadee5f3))


### Documentation

* **agents:** walk into package registrations when re-deriving [#271](https://github.com/mforce/cluckwork/issues/271) ([#790](https://github.com/mforce/cluckwork/issues/790)) ([5fab974](https://github.com/mforce/cluckwork/commit/5fab9747933e12731d9e8f94613413a74c3f6855))
* **mcp:** record the MCP server design and what the spike got wrong ([#785](https://github.com/mforce/cluckwork/issues/785)) ([3fe5a7f](https://github.com/mforce/cluckwork/commit/3fe5a7fe1e6f1b752e4dbfee33a447c8273436bb)), closes [#770](https://github.com/mforce/cluckwork/issues/770)

## [0.1.0](https://github.com/mforce/cluckwork/compare/v0.0.4...v0.1.0) (2026-09-12)


### ⚠ BREAKING CHANGES

* log in by farm code, with per-account email identity ([#532](https://github.com/mforce/cluckwork/issues/532)) (#564)

### Features

* **accounts:** add Account.Slug (farm code), suspend/reactivate, list-accounts verb ([#531](https://github.com/mforce/cluckwork/issues/531)) ([3fe9754](https://github.com/mforce/cluckwork/commit/3fe975454a761b2b29bc9ced67a561ccc5a5260d))
* **accounts:** provision additional farms ([#581](https://github.com/mforce/cluckwork/issues/581)) ([006f298](https://github.com/mforce/cluckwork/commit/006f298aef1ef00ff44f1b7da18f280a9ae6a67b))
* add Aspire local development AppHost ([#567](https://github.com/mforce/cluckwork/issues/567)) ([2c9e6b9](https://github.com/mforce/cluckwork/commit/2c9e6b933de5b570b7a3960277f0c911bac67bbf))
* add configurable worker sale allocation ([#619](https://github.com/mforce/cluckwork/issues/619)) ([0955095](https://github.com/mforce/cluckwork/commit/0955095f3185471a55a6890adfa827bb29dd518e))
* add searchable entity pickers ([#642](https://github.com/mforce/cluckwork/issues/642)) ([60d2053](https://github.com/mforce/cluckwork/commit/60d2053c7eb0394c04439c8eae1a3b0cfc6098a4))
* **api:** provision-account takes an optional --timezone at creation ([#603](https://github.com/mforce/cluckwork/issues/603)) ([#694](https://github.com/mforce/cluckwork/issues/694)) ([a0aee39](https://github.com/mforce/cluckwork/commit/a0aee39c51c3bf0115f581bb8dc9b658d2b40932))
* **audit:** show the sales-line audit payload as a readable Details column ([#745](https://github.com/mforce/cluckwork/issues/745)) ([#749](https://github.com/mforce/cluckwork/issues/749)) ([d26d389](https://github.com/mforce/cluckwork/commit/d26d389dc6a538e2227e4342611d7ab30f0b14a4))
* **auth:** add ApplicationUser.StepUpLogoutEpoch column ([#338](https://github.com/mforce/cluckwork/issues/338)) ([#554](https://github.com/mforce/cluckwork/issues/554)) ([18306ee](https://github.com/mforce/cluckwork/commit/18306ee3c3382eed672bfaefddb92fd007e6f0a4))
* certify over-cap simulation fixture bands ([#633](https://github.com/mforce/cluckwork/issues/633)) ([a67b2e1](https://github.com/mforce/cluckwork/commit/a67b2e1f0c3dba2a8635d76ffc8e4f6ad7e9f58b)), closes [#627](https://github.com/mforce/cluckwork/issues/627)
* **cli:** rename-account verb to change a farm code ([#732](https://github.com/mforce/cluckwork/issues/732)) ([#733](https://github.com/mforce/cluckwork/issues/733)) ([4b70559](https://github.com/mforce/cluckwork/commit/4b7055941e5f1d3502b142985129415de1b5159b))
* **customers:** edit existing customer details ([#625](https://github.com/mforce/cluckwork/issues/625)) ([#626](https://github.com/mforce/cluckwork/issues/626)) ([062a55c](https://github.com/mforce/cluckwork/commit/062a55c88a075fc181cb675c2bd3f8d3ff856e9d))
* **jobs:** single-runner leader gate for the durable job worker ([#271](https://github.com/mforce/cluckwork/issues/271)) ([#555](https://github.com/mforce/cluckwork/issues/555)) ([4148f9b](https://github.com/mforce/cluckwork/commit/4148f9bc97d43c8e4c4811049f5b49360a1a8a63))
* let owners change user email addresses ([#605](https://github.com/mforce/cluckwork/issues/605)) ([842347b](https://github.com/mforce/cluckwork/commit/842347bd0a747b33e6b26a953cfea0733095ab61))
* log in by farm code, with per-account email identity ([#532](https://github.com/mforce/cluckwork/issues/532)) ([#564](https://github.com/mforce/cluckwork/issues/564)) ([68adb62](https://github.com/mforce/cluckwork/commit/68adb621b45567f6526b61f34c13ff728452baac))
* **ratelimit:** distributed IP-keyed auth limiters ([#544](https://github.com/mforce/cluckwork/issues/544)) ([#558](https://github.com/mforce/cluckwork/issues/558)) ([ec14972](https://github.com/mforce/cluckwork/commit/ec1497283787cd63d064d20c28227a3ea3311871))
* **ratelimit:** distributed per-account report concurrency cap with local-ceiling fallback ([#545](https://github.com/mforce/cluckwork/issues/545)) ([#559](https://github.com/mforce/cluckwork/issues/559)) ([1522e4e](https://github.com/mforce/cluckwork/commit/1522e4e99ea3e062dcb735abbdeaab3ae26190ac))
* **sales:** mark discounted lines, total the discount, and show it in the Orders list ([#723](https://github.com/mforce/cluckwork/issues/723), [#724](https://github.com/mforce/cluckwork/issues/724)) ([#741](https://github.com/mforce/cluckwork/issues/741)) ([1a07441](https://github.com/mforce/cluckwork/commit/1a07441b462190d81d3e1b058056c3251c6a21c1))
* **sales:** record list, old and new price in the order-line audit payload ([#722](https://github.com/mforce/cluckwork/issues/722)) ([#742](https://github.com/mforce/cluckwork/issues/742)) ([97c866f](https://github.com/mforce/cluckwork/commit/97c866f0113c7c7bcfe1f01a27b778eab0cb9b12))
* **sales:** refuse an over-ceiling confirm from a Sales user ([#727](https://github.com/mforce/cluckwork/issues/727)) ([#766](https://github.com/mforce/cluckwork/issues/766)) ([8c0792a](https://github.com/mforce/cluckwork/commit/8c0792a5788980652d26d5db88cc37b6386a4273))
* **sales:** show what each order still owes, and filter the list to unpaid ([#771](https://github.com/mforce/cluckwork/issues/771)) ([ca59d68](https://github.com/mforce/cluckwork/commit/ca59d68a0e0dd187d7f5fedc459ceba7b0b939ed))
* **sales:** snapshot the list price on the order line and show the discount ([#734](https://github.com/mforce/cluckwork/issues/734)) ([cffed5e](https://github.com/mforce/cluckwork/commit/cffed5ee5d0c6dd7731aa4fcb102307ff2dc3bbb))
* **sales:** snapshot the product name and unit in the order-line audit payload ([#747](https://github.com/mforce/cluckwork/issues/747)) ([#748](https://github.com/mforce/cluckwork/issues/748)) ([0481c06](https://github.com/mforce/cluckwork/commit/0481c06db80424b1220af23d18eb46d700aada08))
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
* **audit:** show the price a line sold for, not its list price ([#759](https://github.com/mforce/cluckwork/issues/759)) ([e6b37d0](https://github.com/mforce/cluckwork/commit/e6b37d0dc97676e4bef8fa41b4bfbe32a647d76e))
* **audit:** store catalog enums by name and guard the add-item transaction shape ([#751](https://github.com/mforce/cluckwork/issues/751)) ([23609ff](https://github.com/mforce/cluckwork/commit/23609ff9ba88e0f12fb5744b42d1d08358b9256b))
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
* **sales:** keep a line's discount markers agreeing while its price is edited ([#752](https://github.com/mforce/cluckwork/issues/752)) ([#753](https://github.com/mforce/cluckwork/issues/753)) ([c159b4b](https://github.com/mforce/cluckwork/commit/c159b4b8c4c7a41055934454caf8f61a84f18c03))
* **sales:** say which kind of missing list price a line has ([#774](https://github.com/mforce/cluckwork/issues/774)) ([489180e](https://github.com/mforce/cluckwork/commit/489180e7a5718b7781e0e85ecde9dcdb404a525d))
* scope legacy logout to selected farm ([#624](https://github.com/mforce/cluckwork/issues/624)) ([fae8d82](https://github.com/mforce/cluckwork/commit/fae8d82175fb53fa95610a85a7c87779dbd17b42))
* **seed:** drain the daily-entry lock sweep so deep simulation fixtures validate ([#644](https://github.com/mforce/cluckwork/issues/644)) ([730fa23](https://github.com/mforce/cluckwork/commit/730fa238749ef4b816923de64fb7b28c20ad560e)), closes [#638](https://github.com/mforce/cluckwork/issues/638)
* **tenancy:** AccountId is a concurrency token, so the database refuses a detached cross-tenant write ([#562](https://github.com/mforce/cluckwork/issues/562)) ([4d1dfa3](https://github.com/mforce/cluckwork/commit/4d1dfa3729a8d1feea80d1261a27ad1373068e65))
* **tenancy:** AspNetUserRoles carries a tenant column, so a role write naming another farm's user is refused ([#670](https://github.com/mforce/cluckwork/issues/670)) ([fc0552a](https://github.com/mforce/cluckwork/commit/fc0552aef110973be3cee8b369d4c9862045db90))
* **tests:** bump the image-pin allow-list counts for the AppHost LocalPorts tests ([#593](https://github.com/mforce/cluckwork/issues/593)) ([58d3056](https://github.com/mforce/cluckwork/commit/58d30568414f3f6b8f1f5742f005a1b3b63c4420))
* **tests:** the OTLP collector survives a lost port race and ignores traffic that is not an export ([#672](https://github.com/mforce/cluckwork/issues/672), [#676](https://github.com/mforce/cluckwork/issues/676)) ([#677](https://github.com/mforce/cluckwork/issues/677)) ([965c737](https://github.com/mforce/cluckwork/commit/965c73745fc2784bb155cfdf8ff399870ed0c196))
* **web:** a scoped audit view filtered to nothing names both the record and the range ([#666](https://github.com/mforce/cluckwork/issues/666)) ([41bbfe1](https://github.com/mforce/cluckwork/commit/41bbfe12aab8efac6be4aa79e2e8fd74275dfbaf))
* **web:** an abandoned dialog attempt's success no longer hijacks the replacement on Customers, Daily Entry, Flocks, Grades and Products ([#703](https://github.com/mforce/cluckwork/issues/703)) ([#705](https://github.com/mforce/cluckwork/issues/705)) ([85605db](https://github.com/mforce/cluckwork/commit/85605dba84aed27ecf10fce26d6722ee36eef91e))
* **web:** an abandoned dialog attempt's success no longer hijacks the replacement on Inventory, Expenses, History and Stock ([#703](https://github.com/mforce/cluckwork/issues/703)) ([#706](https://github.com/mforce/cluckwork/issues/706)) ([60a4997](https://github.com/mforce/cluckwork/commit/60a49978f2499556117ae9d7be6eb6af4486233d))
* **web:** an abandoned edit's success no longer hijacks the dialog that replaced it on Users ([#703](https://github.com/mforce/cluckwork/issues/703)) ([#710](https://github.com/mforce/cluckwork/issues/710)) ([778faab](https://github.com/mforce/cluckwork/commit/778faab93951300be38cfe13f2f0be5d90585e6a))
* **web:** an abandoned order attempt's success no longer hijacks the dialog that replaced it ([#702](https://github.com/mforce/cluckwork/issues/702)) ([522c699](https://github.com/mforce/cluckwork/commit/522c699e6e424055c93f69b468c9bff00722f680))
* **web:** capture screens open on the flock you last used, and assigning one no longer guesses ([#646](https://github.com/mforce/cluckwork/issues/646)) ([#699](https://github.com/mforce/cluckwork/issues/699)) ([7f8f317](https://github.com/mforce/cluckwork/commit/7f8f31725608e42ac236a52b3bbbcf4cb9b187fc))
* **web:** constrain dialog session helpers to declared scopes ([#715](https://github.com/mforce/cluckwork/issues/715)) ([389e3c8](https://github.com/mforce/cluckwork/commit/389e3c80b8eb1a92474c81bdcaef2281abed6916))
* **web:** date validation gets one boundary table instead of one case per review round ([#666](https://github.com/mforce/cluckwork/issues/666)) ([215f830](https://github.com/mforce/cluckwork/commit/215f830a37509b81497dfc09fa7cced0c51a2015))
* **web:** keep a paged window and an item panel on the user's newest intent ([#645](https://github.com/mforce/cluckwork/issues/645)) ([d81bccf](https://github.com/mforce/cluckwork/commit/d81bccf7c42525735c16076ff768d2ca1c54fc11))
* **web:** keep Sales order panels closed after pending writes ([#711](https://github.com/mforce/cluckwork/issues/711)) ([f0f7492](https://github.com/mforce/cluckwork/commit/f0f749293c443be12cd310b3828339ada1a1987d))
* **web:** keep Sales panels closed after pending Open reads ([#716](https://github.com/mforce/cluckwork/issues/716)) ([620411f](https://github.com/mforce/cluckwork/commit/620411f59077a7438ffb5e8485cb80c8ed91f5ae))
* **web:** make login take the cross-tab cookie lock so a racing refresh cannot restore the wrong session ([#648](https://github.com/mforce/cluckwork/issues/648)) ([ff18beb](https://github.com/mforce/cluckwork/commit/ff18beb9d1cd7dc5f931b992313eb7841f0bf660))
* **web:** make the entity picker read as a search field and focus it on open ([#736](https://github.com/mforce/cluckwork/issues/736)) ([66ef667](https://github.com/mforce/cluckwork/commit/66ef66762afa83360415fbdb922ae2b3a6a864ab)), closes [#735](https://github.com/mforce/cluckwork/issues/735)
* **web:** page truncated customer and movement tables with usePagedList ([7cfe4d6](https://github.com/mforce/cluckwork/commit/7cfe4d6fdd82ac0caf0a70572eab1c5a229ae9e7))
* **web:** reconcile Sales line edits with refreshed orders ([#717](https://github.com/mforce/cluckwork/issues/717)) ([d7dd2c9](https://github.com/mforce/cluckwork/commit/d7dd2c95841afef42fa3d8c02cdc5df319120097))
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
* **agents:** a PR closes its issue from the body, not the title ([#744](https://github.com/mforce/cluckwork/issues/744)) ([39be13c](https://github.com/mforce/cluckwork/commit/39be13ca842453e362c688db7ea73a5edced51cb))
* **agents:** drop the commit and push gate, and require screenshots on UI changes ([#757](https://github.com/mforce/cluckwork/issues/757)) ([6225172](https://github.com/mforce/cluckwork/commit/62251722b905bdb4b1bbbff9157088c9266cf107))
* **agents:** find guards by grepping registry readers; amend issues a PR overtakes ([#580](https://github.com/mforce/cluckwork/issues/580)) ([fe3fde8](https://github.com/mforce/cluckwork/commit/fe3fde8e15a383659c86682c8b7a3887318a2038))
* **agents:** the Playwright specs have been in CI since 2026-08-08 ([#768](https://github.com/mforce/cluckwork/issues/768)) ([68ee612](https://github.com/mforce/cluckwork/commit/68ee612c16cb6b204f20bc3450ccbed61b7b1691))
* **aspire:** record the second local database and pin the AppHost dashboard ports ([#623](https://github.com/mforce/cluckwork/issues/623)) ([713b941](https://github.com/mforce/cluckwork/commit/713b9411f55c0f14f67d020365e8651e0ced075d))
* compress AGENTS.md to one paragraph per rule, and draw the two orders that matter ([#551](https://github.com/mforce/cluckwork/issues/551)) ([997ae8a](https://github.com/mforce/cluckwork/commit/997ae8aea5f360aff9cf884270a3eebf27ad1f27))
* item 7 names each screen's actual initial filter value ([#666](https://github.com/mforce/cluckwork/issues/666)) ([70a53d8](https://github.com/mforce/cluckwork/commit/70a53d8f3a93c7506d292cd6f43b0cb103b489f6))
* multi-farm tenancy decision record and AGENTS/GLOSSARY sync ([#537](https://github.com/mforce/cluckwork/issues/537)) ([#601](https://github.com/mforce/cluckwork/issues/601)) ([2c34771](https://github.com/mforce/cluckwork/commit/2c34771342cac2df26654ac90b2680567f2ffb1b))
* name the scoped filtered-empty key and state the [#653](https://github.com/mforce/cluckwork/issues/653) relationship plainly ([#666](https://github.com/mforce/cluckwork/issues/666)) ([0e93dac](https://github.com/mforce/cluckwork/commit/0e93dac6c70d305a8842ce54c8c4f24356baf2b3))
* note that a PackageReference in Directory.Build.props is invisible to the dependency graph ([4845724](https://github.com/mforce/cluckwork/commit/48457247e4bb1fdb829a3055ba678627a582cf40))
* **plans:** commit the [#722](https://github.com/mforce/cluckwork/issues/722) and [#745](https://github.com/mforce/cluckwork/issues/745) design records ([#754](https://github.com/mforce/cluckwork/issues/754)) ([c942fcd](https://github.com/mforce/cluckwork/commit/c942fcd0de69a7da080df90d31ee81443427eae2))
* record [#579](https://github.com/mforce/cluckwork/issues/579) as won't-fix — suspension is immediate for use, not issuance ([#582](https://github.com/mforce/cluckwork/issues/582)) ([7a3be40](https://github.com/mforce/cluckwork/commit/7a3be4098d673658954736195342e052ca1c0c5f))
* record the [#508](https://github.com/mforce/cluckwork/issues/508) audit ordering key and the tracked-file guard lesson ([#701](https://github.com/mforce/cluckwork/issues/701)) ([08964e9](https://github.com/mforce/cluckwork/commit/08964e98371b6467ebc6381207dd9932ebddc82e))
* **runbooks:** add procedure to rename the default farm's code after upgrade ([#731](https://github.com/mforce/cluckwork/issues/731)) ([2f6e242](https://github.com/mforce/cluckwork/commit/2f6e242f3062cf54f23c2b55c67f611c4190a2cb))
* screenshots of the running SPA in the README ([#550](https://github.com/mforce/cluckwork/issues/550)) ([711488a](https://github.com/mforce/cluckwork/commit/711488a9b32cf59eeacd4e5bdd5aa6586c3f8b50))
* **sim:** commit the dashboard screenshot, capture the palette matrix, and record the [#651](https://github.com/mforce/cluckwork/issues/651)/[#652](https://github.com/mforce/cluckwork/issues/652) conventions ([#660](https://github.com/mforce/cluckwork/issues/660), [#662](https://github.com/mforce/cluckwork/issues/662), [#663](https://github.com/mforce/cluckwork/issues/663), [#664](https://github.com/mforce/cluckwork/issues/664)) ([#665](https://github.com/mforce/cluckwork/issues/665)) ([930ea30](https://github.com/mforce/cluckwork/commit/930ea3085187e19c53e3782b011a15fee8ce5784))
* specify searchable entity picker ([#641](https://github.com/mforce/cluckwork/issues/641)) ([91d4300](https://github.com/mforce/cluckwork/commit/91d43009c45be0ed28ea86447cb8fd6cdbf53c0f))
* split the README into audience-scoped docs and adopt repo-template scaffolding ([#548](https://github.com/mforce/cluckwork/issues/548)) ([b3f3fcf](https://github.com/mforce/cluckwork/commit/b3f3fcf8b7f62289e01132eec9418eaf4dda7e6f))
* surface Aspire local development workflow ([#568](https://github.com/mforce/cluckwork/issues/568)) ([a343baa](https://github.com/mforce/cluckwork/commit/a343baa6cafb7ffc71de0475ae9bab8e6e6c9dfb))
* **web:** record the per-screen idempotency-key policies and runWrite's refresh contract ([#703](https://github.com/mforce/cluckwork/issues/703)) ([#707](https://github.com/mforce/cluckwork/issues/707)) ([8bee651](https://github.com/mforce/cluckwork/commit/8bee651fffee23e2022d1e84067e8ac3a4925a7a))
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
