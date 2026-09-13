# Changelog

## [0.7.3](https://github.com/equinor/osdu-csharp-cli/compare/v0.7.2...v0.7.3) (2026-09-13)


### Bug Fixes

* **generator:** emit fixed strings exactly and range-check fixed integers ([f0966d6](https://github.com/equinor/osdu-csharp-cli/commit/f0966d65e5f2c6d5a352b575ff155408ad9260a5))
* **generator:** refuse fixed body values that could silently not be sent ([4dfc5d4](https://github.com/equinor/osdu-csharp-cli/commit/4dfc5d4192a548fa9dcfa0f2430fb27436273c41))
* **release:** stop a failed hash from publishing an empty checksum ([3270109](https://github.com/equinor/osdu-csharp-cli/commit/327010940fad9b7841de8b385ad5c3f4bcea3f37))
* **search:** send limit 1 from record aggregate and map list records to it ([4a80ba1](https://github.com/equinor/osdu-csharp-cli/commit/4a80ba1fe4e3352fa85296f4df789cd5e5bf72ad))
* **search:** send limit 1 from record aggregate and map list records to it ([e6b25b3](https://github.com/equinor/osdu-csharp-cli/commit/e6b25b359143ce8e970e16aef8f85ec37961cc67))

## [0.7.2](https://github.com/equinor/osdu-csharp-cli/compare/v0.7.1...v0.7.2) (2026-09-09)


### Bug Fixes

* **cli:** accept enum option values in any casing ([1356179](https://github.com/equinor/osdu-csharp-cli/commit/135617942a8ddb8f049147e4df9f5f51417f74fe))
* **cli:** accept enum option values in any casing ([5f60ad2](https://github.com/equinor/osdu-csharp-cli/commit/5f60ad26905f849ae85820ba02613f01f89d5bcd))
* fail closed on an unreadable pin and cover fetch itself with tests ([f6cab4a](https://github.com/equinor/osdu-csharp-cli/commit/f6cab4a6d69d4fc63e6d10650548744ad3e2a882))
* **help:** document --type NONE and mark spec-required options as required ([60137ad](https://github.com/equinor/osdu-csharp-cli/commit/60137aded046ecabb051e961da42ca7002c51d02))
* **help:** document --type NONE and mark spec-required options as required ([c8f63d0](https://github.com/equinor/osdu-csharp-cli/commit/c8f63d0dfee851ca40183f2e3be4bde435344289))
* refuse to generate against fetched specs stamped with a different ref ([9c31a1b](https://github.com/equinor/osdu-csharp-cli/commit/9c31a1bf6c7f1be2fde4515501832d4f290d3832))

## [0.7.1](https://github.com/equinor/osdu-csharp-cli/compare/v0.7.0...v0.7.1) (2026-09-08)


### Bug Fixes

* **config:** accept user as well as username for the default account ([ec12d5e](https://github.com/equinor/osdu-csharp-cli/commit/ec12d5ece44a2e9be0437c358921d26ecd72bfe9))
* **config:** finish the user rename across output, docs and tests ([9337332](https://github.com/equinor/osdu-csharp-cli/commit/9337332f978fc6e965be5633ba35d6ad45a980db))
* **config:** read the default account from user, matching the flag ([c2f600c](https://github.com/equinor/osdu-csharp-cli/commit/c2f600c8c3213d47e59405f149639c24cb3768b2))
* **config:** read the default account from user, matching the flag ([0825931](https://github.com/equinor/osdu-csharp-cli/commit/08259314bb1b6a2bbd7ade54b5080e4d380ea95c))
* **errors:** read the nested body when a service error has no message ([f82f297](https://github.com/equinor/osdu-csharp-cli/commit/f82f2977310501ee5f993878541eab9cd4749cdb))
* **schema:** send the schema on add and surface nested service errors ([14bdbb9](https://github.com/equinor/osdu-csharp-cli/commit/14bdbb9af808df6687279220da2bd1e5a9311e63))
* **schema:** take client 2.2.1 so schema add sends the schema ([621c8b6](https://github.com/equinor/osdu-csharp-cli/commit/621c8b6e09700e3f4c5c3cc0dabbde24731caed9))

## [0.7.0](https://github.com/equinor/osdu-csharp-cli/compare/v0.6.3...v0.7.0) (2026-09-08)


### Features

* **errors:** name the missing role when an endpoint refuses ([d9ebff2](https://github.com/equinor/osdu-csharp-cli/commit/d9ebff23f1e75ef460369f8c0a43e4c98897065d))
* **errors:** name the missing role when an endpoint refuses ([5d1a694](https://github.com/equinor/osdu-csharp-cli/commit/5d1a6947f85a5429e01577aae49da68468330e23))


### Bug Fixes

* **errors:** keep the 403 guidance under --debug and tighten role matching ([394334f](https://github.com/equinor/osdu-csharp-cli/commit/394334f530b66d8ce40ae66acf2198e743b3171f))
* **errors:** only treat service and users prefixed tokens as roles ([c3fe8c8](https://github.com/equinor/osdu-csharp-cli/commit/c3fe8c8416bf3e3dfd3d851d045a77b2eee1ea94))

## [0.6.3](https://github.com/equinor/osdu-csharp-cli/compare/v0.6.2...v0.6.3) (2026-09-08)


### Bug Fixes

* **storage:** render the record ids that record list returns ([74a4750](https://github.com/equinor/osdu-csharp-cli/commit/74a4750cc805c7a0e962e211e42665dda63cd5ed))
* **storage:** render the record ids that record list returns ([53a31a9](https://github.com/equinor/osdu-csharp-cli/commit/53a31a93965ec0839da6709b402c72de1a6f73d8))

## [0.6.2](https://github.com/equinor/osdu-csharp-cli/compare/v0.6.1...v0.6.2) (2026-09-08)


### Bug Fixes

* **auth:** follow the client's MSAL namespace to 2.2.0 ([5279e7d](https://github.com/equinor/osdu-csharp-cli/commit/5279e7d07a9175f173dd1ddbdb76eaec1552eb60))
* **auth:** follow the client's MSAL namespace to 2.2.0 ([e2ab73a](https://github.com/equinor/osdu-csharp-cli/commit/e2ab73a70a919ff0aeaa25bf982f58f503ff59ac))
* **auth:** follow the client's MSAL namespace to 3.0.0 ([8ce5142](https://github.com/equinor/osdu-csharp-cli/commit/8ce514264053450d83baf593049d12255e53a27c))

## [0.6.1](https://github.com/equinor/osdu-csharp-cli/compare/v0.6.0...v0.6.1) (2026-09-06)


### Bug Fixes

* **help:** list config under The CLI itself, not among the resources ([948b8e8](https://github.com/equinor/osdu-csharp-cli/commit/948b8e842eed55e3048ae27635fd17fd77a523a8))
* **help:** list config under The CLI itself, not among the resources ([b646cae](https://github.com/equinor/osdu-csharp-cli/commit/b646caeddc9b69a01d4d21a8de8b95d7b8aba7df))

## [0.6.0](https://github.com/equinor/osdu-csharp-cli/compare/v0.5.1...v0.6.0) (2026-09-06)


### Features

* **config:** add config list, use and show ([a9dde34](https://github.com/equinor/osdu-csharp-cli/commit/a9dde3459ebd1f96f530be4417c28964050c4799))
* **config:** add config list, use and show ([6859622](https://github.com/equinor/osdu-csharp-cli/commit/68596225c930f487f80189bfbab831e451bda729))

## [0.5.1](https://github.com/equinor/osdu-csharp-cli/compare/v0.5.0...v0.5.1) (2026-09-05)


### Bug Fixes

* **help:** `osducs` on its own now prints the help instead of reporting a parse failure ([feafc95](https://github.com/equinor/osdu-csharp-cli/commit/feafc95348a11a212f6addd18ff96bc9812d42f6))
* **help:** `--user` is listed under Common Options, not among each command's own flags ([8621675](https://github.com/equinor/osdu-csharp-cli/commit/86216758818e6bd6a1857404514a364aa439a495))
* **help:** root sections are named for what they contain — `Core resources`, `Wellbore DDMS`, `The CLI itself` ([24c5cb4](https://github.com/equinor/osdu-csharp-cli/commit/24c5cb4581dc4e6c7c83e21a733627f87cfec21c))

## [0.5.0](https://github.com/equinor/osdu-csharp-cli/compare/v0.4.1...v0.5.0) (2026-09-04)


### Features

* **auth:** add --user to choose between signed-in accounts ([22696d4](https://github.com/equinor/osdu-csharp-cli/commit/22696d410ba0ba6765893060bc876be7c58560ba))

## [0.4.1](https://github.com/equinor/osdu-csharp-cli/compare/v0.4.0...v0.4.1) (2026-08-27)


### Dependencies

* upgrade to client 2.0.0 and select the MSAL provider explicitly ([6fa16d4](https://github.com/equinor/osdu-csharp-cli/commit/6fa16d40e2f27e8b7dfa149a179dae39a7c40998))

  Client 2.0.0 made its core package authentication-agnostic: it no longer bundles MSAL, and
  `OsduClient` takes an `ITokenProvider` instead of defaulting to one. The CLI now references
  `Equinor.OsduCsharpClient.Msal` and selects `MsalInteractiveTokenProvider`, which is what
  the old default did. Nothing changes for users of this CLI — same commands, same flags,
  same config, and cached sign-ins survive the upgrade.

## [0.4.0](https://github.com/equinor/osdu-csharp-cli/compare/v0.3.1...v0.4.0) (2026-08-27)


### Features

* **help:** group root commands into named sections ([2f235b7](https://github.com/equinor/osdu-csharp-cli/commit/2f235b71087fcc6a0debac9b03867a41e5fd6584))
* **wellbore:** add bulk data reads for the four bulk-carrying types ([c94d8d3](https://github.com/equinor/osdu-csharp-cli/commit/c94d8d38c2088787a3016094be6fdad58034d69f))
* **wellbore:** cover every DDMS record type, excluding bulk data ([eb4df33](https://github.com/equinor/osdu-csharp-cli/commit/eb4df331dd10a3c1af07b3fe1682c0292d1b1c96))

## [0.3.1](https://github.com/equinor/osdu-csharp-cli/compare/v0.3.0...v0.3.1) (2026-08-26)


### Bug Fixes

* **status:** probe all twelve services, not ten ([920188d](https://github.com/equinor/osdu-csharp-cli/commit/920188d86cb671c69932a694f112094cad1a5a45))

## [0.3.0](https://github.com/equinor/osdu-csharp-cli/compare/v0.2.1...v0.3.0) (2026-08-26)


### Features

* add excluded-fields and reject it alongside returned-fields ([ff1ee46](https://github.com/equinor/osdu-csharp-cli/commit/ff1ee46b0da7e0a3b6e56a47363134e8de9f5b36))
* add record aggregate for distinct-value counts from search ([8611406](https://github.com/equinor/osdu-csharp-cli/commit/861140648bf0f1d99574f806994c5f152ddc1bb3))
* add record headers for the lightweight multi-id header fetch ([e3f33ab](https://github.com/equinor/osdu-csharp-cli/commit/e3f33abce2774867ffe8dc0b7ca2010e4b31ebb1))
* add require-one-of so mutually-optional params fail before the network ([f0d5c6a](https://github.com/equinor/osdu-csharp-cli/commit/f0d5c6af1491b481e8d57b2e316a798602bf7bd9))
* add sort and track-total-count to search, with nested body fields ([73b5fc7](https://github.com/equinor/osdu-csharp-cli/commit/73b5fc7a95567b5a62ecb7af3e8d8d9ecc28cc5c))
* add spatial filtering to search with bbox and radius flags ([53891d1](https://github.com/equinor/osdu-csharp-cli/commit/53891d17fea9a2ec973c4ea794bffabc94e7903e))
* default to the profile the Python CLI has selected ([9d88792](https://github.com/equinor/osdu-csharp-cli/commit/9d88792bf697e0510b466b940a6e008edec5a31f))
* project search results with returned-fields and follow them as columns ([2ac19e0](https://github.com/equinor/osdu-csharp-cli/commit/2ac19e092c4586fd2d95e34a4c515b8b1996da39))
* reject unknown manifest keys, and recover help text a YAML comma had eaten ([1eb7e0e](https://github.com/equinor/osdu-csharp-cli/commit/1eb7e0e7317dafe0e34e26d691569a8ee4076594))
* show HTTP request and response detail under --debug ([6ce72a2](https://github.com/equinor/osdu-csharp-cli/commit/6ce72a2769f3288eb85b14eef06f8c8e74fd8109))


### Bug Fixes

* replace search examples that returned nothing against a real instance ([8831498](https://github.com/equinor/osdu-csharp-cli/commit/8831498da75cc6870ed70c2760030aaf11b54152))
* signpost where search lives and keep line breaks indented in help ([b5f9edd](https://github.com/equinor/osdu-csharp-cli/commit/b5f9edd0814d969f32f175cfd79a8ea8a3661a56))


### Dependencies

* bump the client to 1.1.8 for the bodiless-request content type ([cbbe0b2](https://github.com/equinor/osdu-csharp-cli/commit/cbbe0b2dc3b9f91721b1f7d96fbc8ed6ca180b83))
* bump the client to 1.1.9 so record reads return data ([885c66f](https://github.com/equinor/osdu-csharp-cli/commit/885c66f56524d808d6c9d2ef09385c7e3e5905d4))

## [0.2.1](https://github.com/equinor/osdu-csharp-cli/compare/v0.2.0...v0.2.1) (2026-08-24)


### Bug Fixes

* force bash for the NuGet auth step so it works on Windows runners ([52cef35](https://github.com/equinor/osdu-csharp-cli/commit/52cef35888dd7890201652b4e681baf941957103))
* resolve the release tag from path_tag_names so assets attach ([90b4c1f](https://github.com/equinor/osdu-csharp-cli/commit/90b4c1f30c0888cea7409fdc6d9ee99bbbdd9699))

## [0.2.0](https://github.com/equinor/osdu-csharp-cli/compare/v0.1.0...v0.2.0) (2026-08-24)


### Features

* add shell completion scripts and a working candidate builder ([cb79e98](https://github.com/equinor/osdu-csharp-cli/commit/cb79e98ca4f61de1d3316849b8613ddfcc41a7b0))
* manifest-driven OSDU CLI covering 12 core services ([56a5b93](https://github.com/equinor/osdu-csharp-cli/commit/56a5b93b9f3bf660c33f2eaeb85932f15465f26c))
* read Python osducli profiles and resolve --config by profile name ([dbe84f5](https://github.com/equinor/osdu-csharp-cli/commit/dbe84f515342ac9a3001f8ccb863aecb4b567211))
* rename the binary to osducs to avoid colliding with osducli ([a4261b7](https://github.com/equinor/osdu-csharp-cli/commit/a4261b759ca56e01d8bf9556d320ab2c63436336))
