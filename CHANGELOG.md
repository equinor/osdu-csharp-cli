# Changelog

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
