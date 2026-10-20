# Changelog

## [1.2.1](https://github.com/Blake11/PenBridge/compare/v1.2.0...v1.2.1) (2026-10-20)


### Features

* **android:** editable values, steppers and a default snap on settings sliders ([#221](https://github.com/Blake11/PenBridge/issues/221)) ([a2e2462](https://github.com/Blake11/PenBridge/commit/a2e2462f1978a91b7458fea76ea0a9084ddc2a58)), closes [#217](https://github.com/Blake11/PenBridge/issues/217) [#212](https://github.com/Blake11/PenBridge/issues/212)
* **core:** add a slider model for setting sliders ([#218](https://github.com/Blake11/PenBridge/issues/218)) ([fc48328](https://github.com/Blake11/PenBridge/commit/fc483289a496713b0281f6efc049386a48331ae9)), closes [#215](https://github.com/Blake11/PenBridge/issues/215) [#212](https://github.com/Blake11/PenBridge/issues/212)
* **mac:** editable values, steppers and a default snap on settings sliders ([#223](https://github.com/Blake11/PenBridge/issues/223)) ([9f0608e](https://github.com/Blake11/PenBridge/commit/9f0608ec179e6f6722cf7f3b31ce749959be4eb9)), closes [#216](https://github.com/Blake11/PenBridge/issues/216) [#212](https://github.com/Blake11/PenBridge/issues/212)


### Bug Fixes

* **core:** let a deliberate twist rotate instead of zooming ([#224](https://github.com/Blake11/PenBridge/issues/224)) ([78e422c](https://github.com/Blake11/PenBridge/commit/78e422cd38cfeb1fd50ca50502daaf6bcc86e5dd)), closes [#214](https://github.com/Blake11/PenBridge/issues/214)
* **core:** stabilise the surface test and drop negative zero from sliders ([#222](https://github.com/Blake11/PenBridge/issues/222)) ([e38aa8e](https://github.com/Blake11/PenBridge/commit/e38aa8ee8015f6edd506abeb84442a4a9b86ea3e))
* **mac:** put tip feel's soft and firm labels under the slider ([#226](https://github.com/Blake11/PenBridge/issues/226)) ([c23ef6b](https://github.com/Blake11/PenBridge/commit/c23ef6be742110508ba4ba3345dfc9036e122538)), closes [#225](https://github.com/Blake11/PenBridge/issues/225)


### Documentation

* update the readme for 1.2.1 ([#228](https://github.com/Blake11/PenBridge/issues/228)) ([2a3e006](https://github.com/Blake11/PenBridge/commit/2a3e006fa8155de03d2a623a6faeadbe39dbc8ab)), closes [#212](https://github.com/Blake11/PenBridge/issues/212)

## [1.2.0](https://github.com/Blake11/PenBridge/compare/v1.1.0...v1.2.0) (2026-10-20)


### Features

* **android:** add the mac settings section and follow the active area ([#199](https://github.com/Blake11/PenBridge/issues/199)) ([5146c2c](https://github.com/Blake11/PenBridge/commit/5146c2c523dd3f0746de149ec3d4dfb452afff63))
* **android:** add the surface look and Surface settings ([#162](https://github.com/Blake11/PenBridge/issues/162)) ([271fc13](https://github.com/Blake11/PenBridge/commit/271fc139782e8c5549f8f7d9e869eb3c007c7ee3))
* **core:** apply the phase 5 input settings ([#164](https://github.com/Blake11/PenBridge/issues/164)) ([b5174f2](https://github.com/Blake11/PenBridge/commit/b5174f278ff78cb10dd0c175021f223a8b570839))
* **mac:** add the settings window and region overlay ([#165](https://github.com/Blake11/PenBridge/issues/165)) ([18d8fdb](https://github.com/Blake11/PenBridge/commit/18d8fdb546764060289fa033f7bb1f5a164754d2))
* **mac:** apply the phase 5 settings and sync them with the phone ([#205](https://github.com/Blake11/PenBridge/issues/205)) ([f2d21af](https://github.com/Blake11/PenBridge/commit/f2d21aff5df1b93c9eeb87d232adcfa046fb9f42))
* **mac:** lock the region to the phone's aspect ([#206](https://github.com/Blake11/PenBridge/issues/206)) ([6b5c555](https://github.com/Blake11/PenBridge/commit/6b5c5557264c0a2207342ca6a4f8f2babaac2ab3))
* **protocol:** sync mac settings and the active area (protocol 1.2) ([#168](https://github.com/Blake11/PenBridge/issues/168)) ([ec70e42](https://github.com/Blake11/PenBridge/commit/ec70e42f02eeae6cad98e144718065a2f10a6c27))


### Bug Fixes

* **ci:** isolate serena per agent type and avoid buried rm -rf ([#196](https://github.com/Blake11/PenBridge/issues/196)) ([b12beb9](https://github.com/Blake11/PenBridge/commit/b12beb97efb46a790e11ac3049afc2a4ee0a554f))
* **core:** resend the active area after a surface change ([#203](https://github.com/Blake11/PenBridge/issues/203)) ([5004330](https://github.com/Blake11/PenBridge/commit/50043301e60da1dbab907f5fc41a776464d8e7e9))
* **mac:** ship the full 16-1024 px legacy app icon set ([#198](https://github.com/Blake11/PenBridge/issues/198)) ([2f594d4](https://github.com/Blake11/PenBridge/commit/2f594d4f5660490a403d01fb01521f3664282a9b))


### Documentation

* add ADR-0030 to ADR-0033 for the Phase 5 settings ([#161](https://github.com/Blake11/PenBridge/issues/161)) ([4f3e566](https://github.com/Blake11/PenBridge/commit/4f3e566a31e0c5bc194b3a2428390c3c4e317b58)), closes [#14](https://github.com/Blake11/PenBridge/issues/14)
* add ADR-0034 post-MVP milestones ([#194](https://github.com/Blake11/PenBridge/issues/194)) ([7a15960](https://github.com/Blake11/PenBridge/commit/7a15960d1d227a3b4f741721f41fb63c60e447a1))
* apply the phase 4 retro to the workflow, agents and skills ([#150](https://github.com/Blake11/PenBridge/issues/150)) ([796de46](https://github.com/Blake11/PenBridge/commit/796de46083aa932e9633147fbb63a10d1f3b899b))
* capture phase 5 learnings in skills ([#207](https://github.com/Blake11/PenBridge/issues/207)) ([182b866](https://github.com/Blake11/PenBridge/commit/182b86672e264bee0b6f86f851f424880b3d0724))
* **mac:** note the PDFKit smart zoom scroll-bar pitfall in the probe readme ([#163](https://github.com/Blake11/PenBridge/issues/163)) ([5c5e9cb](https://github.com/Blake11/PenBridge/commit/5c5e9cbe58695564178a763e1400343ad1b6bece)), closes [#144](https://github.com/Blake11/PenBridge/issues/144)
* update the readme for phase 5 settings and 1.2.0 ([#208](https://github.com/Blake11/PenBridge/issues/208)) ([c8b56ab](https://github.com/Blake11/PenBridge/commit/c8b56ab94ef4eeefdda80255b49da36d6289cd78))

## [1.1.0](https://github.com/Blake11/PenBridge/compare/v1.0.0...v1.1.0) (2026-10-15)


### Features

* add the PenBridge app icon on Android and macOS ([#105](https://github.com/Blake11/PenBridge/issues/105)) ([81edc5c](https://github.com/Blake11/PenBridge/commit/81edc5c85492a524ae1b2e3b5d7f3944d7374e26))
* **input:** add trackpad-parity gesture recognition ([#106](https://github.com/Blake11/PenBridge/issues/106)) ([e9e863c](https://github.com/Blake11/PenBridge/commit/e9e863c8b0a741ec1398f7d36a73d71faf66dfbb))
* **input:** relative one-finger cursor like a trackpad (FingerTouches) ([#131](https://github.com/Blake11/PenBridge/issues/131)) ([43f4679](https://github.com/Blake11/PenBridge/commit/43f467905babd53002ce355686f75c8cfedf10ce))
* **input:** two-finger tap follows the macOS smart zoom setting ([#141](https://github.com/Blake11/PenBridge/issues/141)) ([0e4bcd2](https://github.com/Blake11/PenBridge/commit/0e4bcd2cd0ab85d3b7bbb74787db59762f8f854f))
* **mac:** post the relative one-finger cursor, clicks and drags ([#132](https://github.com/Blake11/PenBridge/issues/132)) ([b67a317](https://github.com/Blake11/PenBridge/commit/b67a317a7b6b87bb8187f039ad469185dc894d99))
* **mac:** post trackpad-parity gestures ([#111](https://github.com/Blake11/PenBridge/issues/111)) ([935940c](https://github.com/Blake11/PenBridge/commit/935940c43d87e2e8539963fdf01a6f4afce89e60))


### Bug Fixes

* **input:** cancel gestures on a palm or platform cancel ([#116](https://github.com/Blake11/PenBridge/issues/116)) ([daf0c9b](https://github.com/Blake11/PenBridge/commit/daf0c9b143fa38eec329b55dd3842e51496b1c85))
* **input:** don't let repeated lift frames delay the two-finger right-click ([#143](https://github.com/Blake11/PenBridge/issues/143)) ([14e3708](https://github.com/Blake11/PenBridge/commit/14e3708555872d27d4f98da9abb6736b784fbc11)), closes [#142](https://github.com/Blake11/PenBridge/issues/142)
* **input:** faster two-finger right-click with a separate one-finger click window ([#133](https://github.com/Blake11/PenBridge/issues/133)) ([6fae160](https://github.com/Blake11/PenBridge/commit/6fae1601263282f9c537891ff628fcbf6d3bc6af))
* **input:** order a held two-finger tap before the pen's events ([#115](https://github.com/Blake11/PenBridge/issues/115)) ([5d7255e](https://github.com/Blake11/PenBridge/commit/5d7255e26867ca1eea7eb12173ff29d39513ea26))
* **input:** recover a finger tap whose datagram is lost (protocol 1.1) ([#117](https://github.com/Blake11/PenBridge/issues/117)) ([c460fb1](https://github.com/Blake11/PenBridge/commit/c460fb12c735433daea72c3ebb45fce0af66b3de))
* **input:** relax two-finger tap and double-tap tolerances for smart zoom ([#128](https://github.com/Blake11/PenBridge/issues/128)) ([14ee6ff](https://github.com/Blake11/PenBridge/commit/14ee6ff738268386b0c55229697ed28149f3d671))
* **mac:** close a cancelled scroll so the next scroll's first step isn't doubled ([#119](https://github.com/Blake11/PenBridge/issues/119)) ([90b424f](https://github.com/Blake11/PenBridge/commit/90b424ffdeaa33a90a1a090cd72c86f99293094f))


### Refactoring

* **net:** enforce takeover input-release ordering ([#113](https://github.com/Blake11/PenBridge/issues/113)) ([860c585](https://github.com/Blake11/PenBridge/commit/860c5852d548f9da2432391248df6b716ed846d6))


### Documentation

* add a later Android UX improvements phase to the roadmap ([#96](https://github.com/Blake11/PenBridge/issues/96)) ([612d560](https://github.com/Blake11/PenBridge/commit/612d5604c81af6b96563eb6ae0e29566d0a47114)), closes [#95](https://github.com/Blake11/PenBridge/issues/95)
* add ADR-0023 trackpad-parity gestures on the Mac ([#104](https://github.com/Blake11/PenBridge/issues/104)) ([e37f4da](https://github.com/Blake11/PenBridge/commit/e37f4da7c21ecc2488d213efc867b66b0fbd3658)), closes [#13](https://github.com/Blake11/PenBridge/issues/13)
* add ADR-0026 for a relative one-finger cursor like a trackpad ([#126](https://github.com/Blake11/PenBridge/issues/126)) ([cecf1c5](https://github.com/Blake11/PenBridge/commit/cecf1c58eb549964fb06d59c466b1b589082260e)), closes [#124](https://github.com/Blake11/PenBridge/issues/124) [#125](https://github.com/Blake11/PenBridge/issues/125)
* add ADR-0029 for following the macOS smart zoom setting ([#135](https://github.com/Blake11/PenBridge/issues/135)) ([12df1e3](https://github.com/Blake11/PenBridge/commit/12df1e3589c0220866d722685434e43a8d6814ee)), closes [#134](https://github.com/Blake11/PenBridge/issues/134)
* capture Phase 4 learnings into skills ([#112](https://github.com/Blake11/PenBridge/issues/112)) ([b03703c](https://github.com/Blake11/PenBridge/commit/b03703c1a6d0202a108055826b26ccd75f546c1c)), closes [#13](https://github.com/Blake11/PenBridge/issues/13)
* correct Phase 4 notes left stale by later changes ([#118](https://github.com/Blake11/PenBridge/issues/118)) ([10cd0c6](https://github.com/Blake11/PenBridge/commit/10cd0c62c9798bf5b3cc4765a11b0f52fbc6143b)), closes [#13](https://github.com/Blake11/PenBridge/issues/13)
* record device-test and release learnings in skills ([#97](https://github.com/Blake11/PenBridge/issues/97)) ([915e8ca](https://github.com/Blake11/PenBridge/commit/915e8ca6b549241b78faf3c424501e9198c0fcac)), closes [#12](https://github.com/Blake11/PenBridge/issues/12)
* update the README for Phase 4 and the 1.1.0 release ([#145](https://github.com/Blake11/PenBridge/issues/145)) ([f47ca65](https://github.com/Blake11/PenBridge/commit/f47ca65b66dd0322284f0e54d9616402c3b3bae3))

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
