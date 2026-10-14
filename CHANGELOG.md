# Changelog

## [1.0.0](https://github.com/Blake11/PenBridge/compare/v0.1.0...v1.0.0) (2026-10-14)


### Features

* **android:** add connect flow, QR pairing and app composition ([#82](https://github.com/Blake11/PenBridge/issues/82)) ([269a1d8](https://github.com/Blake11/PenBridge/commit/269a1d8d1f389f93365702cb12837b641cdbd257)), closes [#65](https://github.com/Blake11/PenBridge/issues/65) [#76](https://github.com/Blake11/PenBridge/issues/76) [#78](https://github.com/Blake11/PenBridge/issues/78)
* **android:** add drawing surface, S Pen sampler, ink echo and status overlay ([#69](https://github.com/Blake11/PenBridge/issues/69)) ([0ccf286](https://github.com/Blake11/PenBridge/commit/0ccf28690245a4f928c86c2786b25e69222a39d9)), closes [#64](https://github.com/Blake11/PenBridge/issues/64)
* **android:** add Keystore secret store, NSD discovery and ClientHost ([#72](https://github.com/Blake11/PenBridge/issues/72)) ([117fc01](https://github.com/Blake11/PenBridge/commit/117fc01e7b989d6d94dc72c6d72b49c22c773f20)), closes [#63](https://github.com/Blake11/PenBridge/issues/63)
* **core:** add input filter, coordinate mapper, pen tracker and gesture recognizer ([#4](https://github.com/Blake11/PenBridge/issues/4)) ([2a852b6](https://github.com/Blake11/PenBridge/commit/2a852b618e830b0606c931dff45b59b049af273a)), closes [#8](https://github.com/Blake11/PenBridge/issues/8)
* **core:** add J-PAKE pairing, server identity and input cipher ([#26](https://github.com/Blake11/PenBridge/issues/26)) ([f22b2b3](https://github.com/Blake11/PenBridge/commit/f22b2b36894f7e62963dc5b4ce987ec017e73b68)), closes [#7](https://github.com/Blake11/PenBridge/issues/7)
* **core:** add network engine and SimClient ([#37](https://github.com/Blake11/PenBridge/issues/37)) ([737d048](https://github.com/Blake11/PenBridge/commit/737d0486371c2125e2933e852c6fa74c78a090c3)), closes [#9](https://github.com/Blake11/PenBridge/issues/9)
* **core:** add Phase 1 spec, ADRs 0009-0012 and input sample contracts ([#3](https://github.com/Blake11/PenBridge/issues/3)) ([dd57821](https://github.com/Blake11/PenBridge/commit/dd578213e8f374dab207833bad136f3f7d96672f))
* **input:** finger cursor mode with tap-to-click and side-button right-drag ([#93](https://github.com/Blake11/PenBridge/issues/93)) ([1929578](https://github.com/Blake11/PenBridge/commit/1929578057feee2e8e61eab6d034febd3c54d192)), closes [#90](https://github.com/Blake11/PenBridge/issues/90) [#91](https://github.com/Blake11/PenBridge/issues/91)
* **mac:** add CGEvent tablet injector and display target resolver ([#50](https://github.com/Blake11/PenBridge/issues/50)) ([09303c5](https://github.com/Blake11/PenBridge/commit/09303c500e950dfaf9cc3c7a30006e8fa90350da)), closes [#38](https://github.com/Blake11/PenBridge/issues/38)
* **mac:** add Keychain store, Bonjour advertiser, os_log logging and ServerHost ([#48](https://github.com/Blake11/PenBridge/issues/48)) ([434ca04](https://github.com/Blake11/PenBridge/commit/434ca044565bf40904a591be9f68c1c5fdfa3a29)), closes [#39](https://github.com/Blake11/PenBridge/issues/39)
* **mac:** add menu-bar shell, global pause hotkey and app composition ([#57](https://github.com/Blake11/PenBridge/issues/57)) ([72c0d4b](https://github.com/Blake11/PenBridge/commit/72c0d4b12fb82d4fc2f5a24178535599654b40b0)), closes [#40](https://github.com/Blake11/PenBridge/issues/40)
* **mac:** add pairing window with QR, PIN and countdown ([#56](https://github.com/Blake11/PenBridge/issues/56)) ([d8ae8e5](https://github.com/Blake11/PenBridge/commit/d8ae8e55bfdce424016c07372e9f4429f02843fb)), closes [#41](https://github.com/Blake11/PenBridge/issues/41)
* **net:** add opt-in latency diagnostics and latency-report script ([#87](https://github.com/Blake11/PenBridge/issues/87)) ([221d64c](https://github.com/Blake11/PenBridge/commit/221d64c5cc444afd9d1a701f42224e58d37bbd70)), closes [#66](https://github.com/Blake11/PenBridge/issues/66)
* **protocol:** add wire-format layer for control frames, input datagrams and discovery ([#5](https://github.com/Blake11/PenBridge/issues/5)) ([c848ce2](https://github.com/Blake11/PenBridge/commit/c848ce23083a5ad2ab4c59b10918f738764806be))


### Bug Fixes

* **android:** auto-detect JDK and Android SDK for local builds ([#34](https://github.com/Blake11/PenBridge/issues/34)) ([260e260](https://github.com/Blake11/PenBridge/commit/260e26064a44667a76ad6084663d6f45714c1e96)), closes [#33](https://github.com/Blake11/PenBridge/issues/33)
* **ci:** add android-run --app-id and android-device connect --auto ([#79](https://github.com/Blake11/PenBridge/issues/79)) ([c631f80](https://github.com/Blake11/PenBridge/commit/c631f80e34d9b6ec8b35eefa31d06b8543799169)), closes [#77](https://github.com/Blake11/PenBridge/issues/77)
* **ci:** allow security, input, net, simclient, windows, spec and release scopes ([#44](https://github.com/Blake11/PenBridge/issues/44)) ([8c4bae9](https://github.com/Blake11/PenBridge/commit/8c4bae978a984551fa828c6f7559a2f966a089b6)), closes [#24](https://github.com/Blake11/PenBridge/issues/24)
* **ci:** downgrade the Xcode first-launch check in doctor to a warning ([#85](https://github.com/Blake11/PenBridge/issues/85)) ([ac84941](https://github.com/Blake11/PenBridge/commit/ac84941dcff360307ab550d50c7d271e15a9982f)), closes [#25](https://github.com/Blake11/PenBridge/issues/25)
* **input:** hold back lone fingers and reject palms by contact size ([#73](https://github.com/Blake11/PenBridge/issues/73)) ([b47a86a](https://github.com/Blake11/PenBridge/commit/b47a86a1920e779130f6cd16d98ed20cc02e9538)), closes [#68](https://github.com/Blake11/PenBridge/issues/68)
* **ios:** mark BouncyCastle trimmable so head Release builds pass ([#30](https://github.com/Blake11/PenBridge/issues/30)) ([9e139ed](https://github.com/Blake11/PenBridge/commit/9e139edd0883d040511bb4c12d8aa82064eba765)), closes [#27](https://github.com/Blake11/PenBridge/issues/27)
* **mac:** keep BouncyCastle pairing code in trimmed head builds ([#47](https://github.com/Blake11/PenBridge/issues/47)) ([7efcdff](https://github.com/Blake11/PenBridge/commit/7efcdffc896bfa9b39dc80cd14726ccc1cead114)), closes [#46](https://github.com/Blake11/PenBridge/issues/46)
* **mac:** sign local builds with the dev identity and keep Debug secrets out of the Keychain ([#60](https://github.com/Blake11/PenBridge/issues/60)) ([bc94ff2](https://github.com/Blake11/PenBridge/commit/bc94ff2295ffb2c3b73cb213c44d034f0eb372bc)), closes [#55](https://github.com/Blake11/PenBridge/issues/55)
* make first platform builds run and harden release packaging ([9128355](https://github.com/Blake11/PenBridge/commit/91283557b1663c2619fa99677a8011019a99f1b2))
* **net:** report TakenOver reliably when a session is taken over ([#80](https://github.com/Blake11/PenBridge/issues/80)) ([d695b30](https://github.com/Blake11/PenBridge/commit/d695b3039e9fb10bc5f403c0e224425fa1219406)), closes [#74](https://github.com/Blake11/PenBridge/issues/74)


### Performance

* **mac:** build the Release app with NativeAOT and trim idle memory ([#62](https://github.com/Blake11/PenBridge/issues/62)) ([869ee1b](https://github.com/Blake11/PenBridge/commit/869ee1b01c9d73e103894d5aeb863ec286da1c85)), closes [#42](https://github.com/Blake11/PenBridge/issues/42)


### Documentation

* add Phase 2 Mac server spec and ADR-0016 ([#43](https://github.com/Blake11/PenBridge/issues/43)) ([0758449](https://github.com/Blake11/PenBridge/commit/07584491004685fd019af1dc145fe836e24c6ccd)), closes [#11](https://github.com/Blake11/PenBridge/issues/11)
* add Phase 3 Android client spec and ADR-0019 ([#67](https://github.com/Blake11/PenBridge/issues/67)) ([74cc6e8](https://github.com/Blake11/PenBridge/commit/74cc6e832853a8b531e513da29fa12f46b231484)), closes [#12](https://github.com/Blake11/PenBridge/issues/12)
* adopt issue-driven workflow and plan Windows host (ADR-0014, ADR-0015) ([#29](https://github.com/Blake11/PenBridge/issues/29)) ([4f6634e](https://github.com/Blake11/PenBridge/commit/4f6634e4ad91e3c19939f264b093f8a05335b9d4))
* capture Phase 1 learnings into skills ([#45](https://github.com/Blake11/PenBridge/issues/45)) ([3a32b9d](https://github.com/Blake11/PenBridge/commit/3a32b9dfedf7878680349709364324b314dc7b95)), closes [#10](https://github.com/Blake11/PenBridge/issues/10)
* record ADR-0017 for Debug-build file secret store ([#58](https://github.com/Blake11/PenBridge/issues/58)) ([41b64b4](https://github.com/Blake11/PenBridge/commit/41b64b4d5126f0604cea86dbf3d09c6f7a4c67ee)), closes [#55](https://github.com/Blake11/PenBridge/issues/55)
* record Phase 2–3 learnings in skills and spec ([#84](https://github.com/Blake11/PenBridge/issues/84)) ([2a40f33](https://github.com/Blake11/PenBridge/commit/2a40f33582a79488985fa3deeb2120b467ec32a0)), closes [#12](https://github.com/Blake11/PenBridge/issues/12)
* showcase README with how-it-works diagram ([#32](https://github.com/Blake11/PenBridge/issues/32)) ([5395bc1](https://github.com/Blake11/PenBridge/commit/5395bc185e77861610c3f6e74197ad0dafdc508d)), closes [#31](https://github.com/Blake11/PenBridge/issues/31)
* update README for delivered phases 1 and 2 ([#75](https://github.com/Blake11/PenBridge/issues/75)) ([4c78ebf](https://github.com/Blake11/PenBridge/commit/4c78ebfa93363565b329be3638700ccb9de13887)), closes [#12](https://github.com/Blake11/PenBridge/issues/12)
* update README for Phase 3 and the 1.0.0 release ([#94](https://github.com/Blake11/PenBridge/issues/94)) ([e14dda1](https://github.com/Blake11/PenBridge/commit/e14dda1296f68da0ec6d38045e702449adf39d27))
