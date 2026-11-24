# Third-party notices

The application's own source code is licensed GPL-3.0-only (`GPL-3.0-only.txt`).
Native C# and Swift adaptations of the projects below are combined GPL-3.0-only code;
the adapted original portions keep their original licenses and attributions.

No executable, server, account database or Node/Python/Go runtime
from the adapted business projects is bundled or executed. The client-side film search
program described below is a static adaptation, not the reference server runtime. Runtime/library dependencies
are listed separately below. Public protocol keys, request-signing
constants and device-profile parameters taken from these sources are client
protocol parameters, not user credentials, and are not an endorsement of their
cryptographic design. No device-registration bypass, anti-bot simulation,
media-unlocking or challenge bypass is copied. The opt-in pan runtime's credential
refresh protocols and the on-demand official Watchman SDK's execution trust
boundary are described below.

## MeowFilm server — protocol reference

The TMDB/Douban runtime's independently implemented Douban adapter follows the
Rexxar request and HTML-search response contracts of the same-owner
`jenfonro/meowfilm-server` repository, reference commit
`934a03e5aeda7edbcef0cfb037734fed93bfe881`:
`Meowfilm/backend/server/metadata/douban/{rexxar.go,search_html.go,proxy.go}`.
No server executable, database/cache implementation or challenge solver is
included. That reference snapshot contains no separate license notice; this
entry records provenance, not a claim that it grants a third-party license.
The new runtime implementation remains application source under GPL-3.0-only.

## meowfilm-server — project search behavior reference

- Source: `jenfonro/meowfilm-server` `934a03e5aeda7edbcef0cfb037734fed93bfe881`.
- Adapted portions: `Meowfilm/frontend/src/shared/searchRuntime.js`,
  `tmdbRaw.js` and `normalize.js`, for the pure search normalization, grouping, ranking, card metadata
  and TMDB detail projection in `packages/film-search/film-search.js`.
- The adaptation adds complete source-page consumption and stable cross-page
  appending. It runs in the clients' native JavaScript engine, without reference
  server endpoints, credentials or playback implementations.
- The referenced tree contains no separate license file. This entry records
  provenance, not an additional upstream license or redistribution permission.
  The application's own source remains covered by the project license above.

## meowfilm-server Reader — novel behavior reference

- Source: the same-owner `jenfonro/meowfilm-server` repository at
  `934a03e5aeda7edbcef0cfb037734fed93bfe881`, `Reader/`.
- `packages/novel-rules/novel-rules.js` adapts identity normalization from
  `src/utils/bookIdentity.js` and the fixed simplified/traditional character
  pairs from `src/utils/chinese.js`. Native novel pages and source-runtime
  integration follow the Reader's book, chapter and reading behavior; no Vue
  application, browser database or reference server executable is bundled.
- Native source/rule managers and form descriptors adapt the same Reader's
  `src/data/bookSourceEditorConfig.js`, `bookSourceEditorMapper.js`,
  `replaceRules.js` and `replaceRuleImport.js`; all six Legado tabs retain the
  original field labels, order and row counts. The native shared controls are
  UIKit/WinUI implementations, not a bundled web view.
- The referenced tree contains no separate license notice. This records
  provenance, not an additional third-party license grant. Application-owned
  adaptations remain under GPL-3.0-only.

## Microsoft WebView2 SDK — BSD-3-Clause

- NuGet package: `Microsoft.Web.WebView2` `1.0.4314.35`, locked in the
  Windows capability project and its package lock.
- Copyright (C) Microsoft Corporation. All rights reserved.
- Original package files are preserved byte-for-byte as `WebView2-LICENSE.txt`
  and `WebView2-NOTICE.txt`.
- Used by the Windows native host for explicit book-source login, verification
  and dynamic-page requests. The SDK is not the Microsoft Edge WebView2 Runtime;
  the application uses an installed compatible Runtime and does not bundle or
  silently install that browser runtime.

## Microsoft.Data.Sqlite — MIT

- NuGet packages: `Microsoft.Data.Sqlite` and `Microsoft.Data.Sqlite.Core`
  `10.0.15`, locked in the Windows platform project and its consumers.
- Copyright (c) .NET Foundation and Contributors. The upstream MIT text is
  identical to the existing `dotnet-runtime-MIT.txt`, which is reused.
- Source: `https://github.com/dotnet/dotnet`, package repository commit
  `8e0f8ed2312954727f8919d7d1c2f78b483b4ee7`.
- The Windows native repositories use this ADO.NET SQLite provider.

## SQLitePCLRaw / SQLite — Apache-2.0 / public domain

- NuGet packages: `SQLitePCLRaw.bundle_e_sqlite3`, `SQLitePCLRaw.core`,
  `SQLitePCLRaw.provider.e_sqlite3` and `SQLitePCLRaw.lib.e_sqlite3` `2.1.13`.
- Copyright 2014-2024 SourceGear, LLC. Package license: `Apache-2.0.txt`.
  Source: `https://github.com/ericsink/SQLitePCL.raw`.
- The Windows `win-x64` publish uses the package's
  `runtimes/win-x64/native/e_sqlite3.dll`; other platform assets are not
  application runtime dependencies.
- The SQLite engine source is public domain. Its upstream license information
  is preserved in `sqlite-LICENSE.md`, from
  `https://github.com/sqlite/sqlite/blob/version-3.53.3/LICENSE.md`.
  SQLite's build-only third-party configuration scripts are not distributed.
- iOS ships one `MeowSQLite.framework` shared by the Swift repositories and the
  embedded Node binding. Its public-domain SQLite 3.53.4 amalgamation comes
  from `https://www.sqlite.org/2026/sqlite-amalgamation-3530400.zip`. The
  source hashes and compile options are recorded in `native/SQLite/sqlite.json`.

## iWebPlus / iWebPlayer — Apache-2.0

- Source: `songloft-plugin-iwebplus` `b900c259d991a73768cc36a28b48539b31961668`.
- Derived from iWebPlayer by birdstudy-nj and contributors, snapshot
  `songloft-org/songloft-plugin-iwebplayer` `731b2517ad71fde1ba7250b24ffb9798cf01128e`.
- Modifications Copyright 2026 jenfonro and iWebPlus contributors.
- License: `Apache-2.0.txt`. Original NOTICE: `iwebplus-NOTICE.txt`.

Adapted portions:

- Accounts: `src/{netease,qq,kugou}.ts`; WY SMS admission and lifecycle from
  `src/netease.ts::{smsReservation,request,setupNeteaseRoutes}` and
  `static/{netease-client,browse-settings}.js`; profile/entitlement freshness from
  `src/netease.ts::{getNeteaseProfile,entitlementSnapshot,getNeteaseEntitlements,cachedAccount}`,
  `src/qq.ts::{membershipProducts,qqMembershipSnapshot,membershipEntry,profileEntry,readQQCache}`
  and `src/kugou.ts::{accountProfile,kugouMembershipSnapshot,membershipDeadline,accountMembership,cachedAccount}`;
  QR lifecycles from `src/qq.ts`, `static/{qq-client,kugou-client,browse-settings}.js`,
  `src/kugou.ts::{anonymous,login,setupKugouRoutes}` and `src/kugou-module.ts`.
- Platforms: `packages/music-platform/src/providers/{wy,tx,kg,wy-discovery,kg-discovery}.mjs`
  adapt `static/discovery-normalize.js`, metadata, content dispatch and personal reads from
  `static/netease-client.js`, `src/netease.ts`, `src/qq-library.ts`, `src/qq-music.ts`,
  `src/kugou.ts` and `src/kugou-play.ts`. Adapted Apache-2.0 portions retain that
  license within the GPL-3.0-only application. Native song repository and playback
  behavior refer to `static/song-identity.js`; the current permanent-ID and fixed-baseline
  matching policy is application code. Chorus recipes in the same Node providers
  adapt `static/chorus-marker.js`, `src/kugou-media.ts` and `src/qq-music.ts`.
- PlaybackCache: `static/source-cache.js`, `static/audio-file-cache.js` and
  `static/chorus-marker.js`.
- Lyrics: `packages/music-platform/src/lyrics.mjs` adapts the platform text conversion
  from `src/{qq-music,kugou-media,netease}.ts` and
  `static/{netease-client,kugou-lyrics,lyric-timing,player}.js`; adapted KRC portions
  remain Apache-2.0. Generic native timing and presentation remain in the clients.
- Library: queue, song identity, quality, history, file-cache policy, lyric timing,
  decoded-KRC translation, display positioning and lyric client/cache policy, from
  `static/{lyric-timing,kugou-lyrics,lyrics,player}.js` and the Netease/QQ/KuGou lyric
  client methods. The timing and decoded-KRC algorithm bodies are unchanged.
- Feedback: Node `packages/music-platform/src/{feedback,playback-log}.mjs` and native
  feedback owners adapt the lifecycle guards and WY Weblog mapping from
  `static/playback-feedback.js`, the feedback route in `src/netease.ts`, `src/qq-feedback.ts`
  and `src/kugou-feedback.ts`; common Node transaction fixtures use synthetic vectors from
  `tests/{netease,qq-feedback,kugou-feedback}.test.cjs`.
- Sync: `MusicSyncAdapter.cs`.
- Windows: `MusicPlaybackIcon.cs` adapts the heart/FM SVGs in `static/index.html`
  and all 42 scene SVGs in `static/fm-icons.js` into native WinUI path geometry,
  retaining their Apache-2.0 attribution. Reference package:
  `iwebplus.jsplugin.zip`, SHA-256
  `325524de00243b7e01779c41b92a07ac601091e5b1454244859cdc71f2da26a8`.
  SVG paths, fill rules, stroke proportions and text transforms are preserved;
  text uses system fonts. The ordinary playback mode artwork follows the supplied
  user references, and the additional `HOT_SONG` flame is local artwork.
- iOS: music interface and behavior in `native/MeowKit/Sources/MeowMusicFeature` and
  `native/MeowKit/Sources/MeowUI`; queue appearance and drag admission follow
  `static/queue-sheet.{js,css}` and `static/list-reorder.js`, with native UIKit
  movement and presentation. Record and tonearm artwork adapts
  `static/themes/starlight/{disc,tonearm}.svg`, `scripts/generate-starlight-assets.mjs`
  and `static/{player-layout.css,player-theme.js,themes/starlight.css}` with native
  Core Graphics and Core Animation. The theme-colored default record cover follows
  `static/utils.js` (`iwebplus-default-cover`) and `static/app-colors.css`.
  Capsule title dragging and settlement follow `static/mini-swipe.js` and
  `static/ui-motion.js`, with native UIKit gesture and animation lifetimes.
  `native/MeowKit/Sources/MeowMusicFeature/Playback/UIKit/MusicPlaybackModeIcon.swift`
  adapts the heart/FM SVGs in `static/index.html` through the confirmed 24-unit
  `MusicPlaybackIcon.cs` paths into native Core Graphics, retaining their
  Apache-2.0 attribution.
  Ported files keep their SPDX and author notices.

## MeowMusic — GPL-3.0-only

- Source: `meowmusic` `cc22a7ee6eb7d02532df59ced788be0205d450ea`.
- License: `GPL-3.0-only.txt`. Original NOTICE: `meowmusic-NOTICE.txt`. Portions that
  MeowMusic derives from iWebPlus remain Apache-2.0 (`Apache-2.0.txt`).

Adapted portions:

- Library: storage separation and failure-atomic transaction boundaries; optional
  already-decrypted QQ romanization passthrough follows
  `backend/internal/platform/qq/music.go` (merging/rendering romanization is not implemented).
- Platforms (Kuwo/Migu public catalog cross-checks):
  `backend/internal/platform/kuwo/{client,catalog,playlist,detail_page}.go` and
  `backend/internal/platform/migu/{client,catalog,playlist}.go`; the data-envelope parser
  is rewritten in `packages/music-platform/src/literal.mjs` as a data-only literal
  reader without expression evaluation; current KW/MG recipes are in `src/providers/{kw,mg}.mjs`.

## NeteaseCloudMusicApiEnhanced/api-enhanced — MIT

- Audited protocol pin: `19c7dad6db7d660954c7a615a686ff0acb030f91`, selected from upstream
  commit metadata at or before the iWebPlus commit; not an embedded dependency or executable.
- Copyright (c) 2013-2022 Binaryify. License: `api-enhanced-MIT.txt`.

Adapted portions:

- Accounts: `module/login_{status,qr_key,qr_check,qr_create}.js`; SMS via
  `module/{captcha_sent,login_cellphone,login_status}.js`, `util/{option,request}.js` and
  `util/config.json` (captcha branch only; no password login or verification bypass);
  profile/entitlement via `module/vip_info_v2.js`.
- Platforms: `packages/music-platform/src/protocols.mjs` translates the WEAPI/EAPI recipes from `util/crypto.js`,
  checked against `util/request.js` and `util/config.json`, with endpoint fields from the
  corresponding `module/*.js`; `src/providers/wy.mjs` uses `module/artists.js`
  (blob `e403fe8512f85565d4113e0fc54584bf4b17f33e`); `src/xeapi.mjs` translates
  `module/register_xeapikey.js`, `util/xeapiKey.js`, `util/crypto.js` and `util/request.js`
  using Node cryptography; `src/providers/wy.mjs` adapts `module/song_music_detail.js` and
  `module/song_url_v1.js`; chorus metadata uses `module/song_chorus.js`.
- Lyrics: `src/providers/wy.mjs` adapts `module/lyric_new.js`.
- Feedback reuses the WY request encryption above.
- Subscription/token integration additionally follows the inspected `v4.41.1`
  reference's `module/playlist_subscribe.js`, `module/register_checktoken_v2.js`
  and `util/request.js`. This is a separate reference from the earlier protocol
  pin above. `packages/netease-token` adapts the Watchman initialization and token
  request sequence; its Worker lifecycle, cancellation and runtime contract are
  application code. The adapted portions retain the Binaryify MIT attribution
  and `api-enhanced-MIT.txt`; that license does not cover the remote official SDK.

## L-1124/QQMusicApi — GPL-3.0-or-later

- Pins: `27861e51432ea6b6e88dc35c4e8c8e239c0339e8` (version 0.8.2, the upstream base named by
  iWebPlus) and the separately audited `2698673d9b2e438bca7a0e06385ead7a4cdaeda9`
  (used by iWebPlus for its report adapter; not asserted to be bundled by iWebPlus).
- Upstream authors and contributors retain ownership. License: `GPL-3.0-only.txt`.
- `algorithms/tripledes.py` is separately GPL-3.0-only,
  SPDX-FileCopyrightText: Copyright (c) 2024 沉默の金 <cmzj@cmzj.org>. `packages/music-platform/src/qrc.mjs`
  retains that attribution and license. Its source comments cite WXRIW/QQMusicDecoder
  `0837a3a1281e58f6db3e3e1dcb1d5441fb0ac268`, which is not copied.

Adapted portions:

- Accounts: `modules/login.py::check_expired`, `modules/user.py::get_homepage`,
  `models/{request,user}.py`, `core/{executor,response,versioning}.py`; QR via
  `qqmusic_api/modules/login.py::{_get_qq_qr,_check_qq_qr,_authorize_qq_qr,_validate_result}`,
  `qqmusic_api/models/{login,request}.py` and `qqmusic_api/utils/common.py::hash33`;
  profile/entitlement via `qqmusic_api/modules/user.py::get_vip_info` and
  `qqmusic_api/models/user.py::{UserVipInfoResponse,VipIdentity,VipUserInfo}`.
- Platforms: read-only recipes from `qqmusic_api/modules/{search,song,songlist,user}.py`,
  `models/{base,search,songlist,user}.py`, `utils/common.py::get_searchID` and the response/versioning envelope;
  `user.get_fav_song`; `song.get_song_urls`; `packages/music-platform/src/{clients,protocols}.mjs`
  and `src/providers/tx.mjs` adapt `qqmusic_api/utils/{device,qimei,android_session}.py`,
  `qqmusic_api/core/versioning.py`, `qqmusic_api/models/request.py`,
  `qqmusic_api/modules/{login,user,song}.py` and `tests/test_qimei.py` vectors.
- Lyrics: `modules/lyric.py`, `models/lyric.py`, `algorithms/__init__.py` and
  `algorithms/tripledes.py`.
- Feedback: `packages/music-platform/src/feedback.mjs` adapts
  `web/src/routes/report.py`, `qqmusic_api/modules/song.py`,
  `qqmusic_api/core/versioning.py`, `qqmusic_api/utils/common.py` and vectors in
  `iwebplus/tests/test_routes.py`.

## MakcRe/KuGouMusicApi — MIT

- Pin: `514f870db7e60ca37bc5535be5da36d32b571c6f`.
- Copyright (c) 2023 MakcRe. License: `KuGouMusicApi-MIT.txt`.

Adapted portions:

- Accounts: `module/user_detail.js`, `util/{crypto,request}.js`; QR via
  `module/{login_qr_key,login_qr_check,login_qr_create}.js`,
  `util/crypto.js::{rsaEncrypt2,playlistAesEncrypt,playlistAesDecrypt}`,
  `util/helper.js::{signatureWebParams,signatureAndroidParams}`,
  `util/request.js::createRequest`, `util/util.js::calculateMid`,
  `util/config.json::{appid,srcappid,clientver}` and `server.js` device defaults;
  profile/entitlement via `module/user_vip_detail.js`.
- Platforms: `module/{search,audio,playlist_track_all,playlist_track_all_new,user_playlist}.js`,
  `util/{helper,request}.js`, `util/config.json`; `module/song_url.js`; the `song_climax`
  chorus recipe; `packages/music-platform/src/{clients,protocols}.mjs` adapts `module/register_dev.js`
  and its fixed crypto/request helpers with the same retained MIT notice.
- Discovery: `packages/music-platform/src/providers/kg-discovery.mjs` adapts
  `module/{top_playlist,top_song,playlist_tags,rank_list,rank_audio}.js` and the fixed
  signing helpers above; native clients consume the common directory, tags and rank-audio results.
- Lyrics: `src/providers/kg.mjs` and `src/lyrics.mjs` adapt
  `module/{search_lyric,lyric}.js` and `util/{request,util}.js`; Node zlib replaces pako.
- Feedback: `packages/music-platform/src/feedback.mjs` contains the current KG recipe,
  with the iWebPlus `src/kugou-feedback.ts` attribution above. Reference pin
  `e19a52a91b9ceb78d9acce768c357b61be9586d5` remains a separately inspected API source,
  not a bundled dependency. Node implementation does not imply native integration or
  verified upstream availability; the Windows adapter currently refuses KG feedback.

## SkiaSharp and Skia — MIT and bundled third-party notices

- Windows artwork and novel image decoding use NuGet `SkiaSharp` and
  `SkiaSharp.NativeAssets.Win32` version `4.155.0`, source
  `mono/SkiaSharp` commit `d25e7fa628a2576e50ecc42004cc4fd03c6657f5`.
- Copyright Microsoft Corporation and the respective Skia and codec contributors.
- `SkiaSharp-MIT.txt` is the unmodified package `LICENSE.txt`;
  `SkiaSharp-THIRD-PARTY-NOTICES.txt` is the unmodified native package
  `THIRD-PARTY-NOTICES.txt`, including Skia and libwebp attribution.
- The Windows license bundle includes both files. Native runtime assets are
  resolved from the pinned NuGet graph for `win-x64`; no system codec installation
  or runtime download is used.

## LX Music Desktop — Apache-2.0

- Source: `lyswhut/lx-music-desktop` `ad95d5091c9ed689fa72b5e5c849df65f5a679ce`.
- Copyright lyswhut and LX Music contributors. License: `Apache-2.0.txt`.

Adapted portions (Kuwo/Migu public catalog in `packages/music-platform/src/providers/{kw,mg}.mjs`,
`src/lyrics.mjs` and the common song metadata mapper):

- `src/renderer/utils/musicSdk/kw/{musicSearch,songList,util,index,lyric}.js`
- `src/renderer/utils/musicSdk/mg/{musicSearch,songList,musicInfo}.js` and `utils/index.js`

## catpawrunner — MIT

- Source: `hjdhnx/catpawrunner` `a7b29ea3ed9bcc05fa94105013d8f807f13152b8`.
- Package metadata author: 道长. The reviewed checkout declares MIT in `package.json`
  and does not contain a standalone license file.
- License: `catpawrunner-MIT.txt`.

Adapted portions:

- Cat startup and service compatibility were informed by `src/util/onlineRuntime.js`, but
  were rewritten for directly executed Node Worker scripts, a controlled module
  interface, installation private volume and per-instance loopback listener. The
  original unrestricted host `require`, `fs`, `process` and child-process path is
  not copied. A Worker or vm context is not a malicious-code security sandbox.
- Management protocol discovery and field mapping in `cat-management.js` adapt the route
  families represented by `src/plugins/api/onlineScriptAdapters.js`; requests go directly
  to the selected installation's current managed endpoint.
- Provider capture, Cat root-level extension and display metadata behavior in
  `pan-interceptor.js`, `pan-placeholders.js` and `cat-output.js` were informed by
  `onlineRuntime.js`, `runtimePanCapture.js`, `panmockDetailCodec.js` and `panDisplayMeta.js`.
  The Quark/UC placeholder fields, Baidu share verification cookie and list shape,
  Tianyi numeric share/file binding and Mobile nested/AES-CBC placeholder protocol
  adapt the reference interception behavior to an invocation-local capture scope;
  unknown requests fail locally instead of being sent to provider endpoints.
  Those placeholder protocols were checked against `CatPawRunner/src/util/onlineRuntime.js`
  in `jenfonro/meowfilm-server` snapshot `934a03e5aeda7edbcef0cfb037734fed93bfe881`.
  That script-capture layer does not include provider SDKs. The separately selectable
  pan runtime below implements the provider protocols directly.

## Built-in pan runtime — protocol references

`packages/pan-runtime` was written for this application's Node service and lifecycle
contract, after consulting `jenfonro/meowfilm-server` snapshot
`934a03e5aeda7edbcef0cfb037734fed93bfe881`:

- `CatPawRunner/src/plugins/api/pan{Baidu,Quark,Uc,189,139}.js` and the corresponding
  `Meowfilm/backend/server/netdisk/{baidu,quark,uc,189,139}.go` protocol implementations.
  The CatPawRunner copy declares MIT and author 道长 in its package metadata;
  the existing `catpawrunner-MIT.txt` attribution applies to those adapted portions.
  The snapshot above identifies the reference aggregation repository, not an
  independently verified CatPawRunner upstream commit.
- Provider endpoint paths, client/device constants, request/response fields, QR
  authentication sequences, necessary selected-file transfers and playback mappings
  follow those references. Mobile AES-CBC framing, the MD5 request-signing formula
  and XML token refresh are protocol adaptations, not newly invented cryptography.
  The implementation uses Node crypto, not copied AES/MD5/RSA library bodies.
- Traversal, exact structured file references, service ownership, account revisions,
  cancellation, shared in-flight work, plaintext atomic storage and offline regression
  fixtures are implemented for the new contract. No reference server, database,
  browser automation, daemon, script engine, daily directory deletion or media relay
  is bundled.

Baidu's exact transfer-result schema (`extra.list`, with `from`,
`from_fs_id`, `to` and `to_fs_id`) was additionally checked against
`komorebiCarry/BaiduPCS-Rust@2456e51bbeed66da13997d96b7dbd4859533f7cb`
(`backend/src/netdisk/client.rs` and `backend/src/netdisk/share/common.rs`,
Apache-2.0), and corroborated by
`cool2528/GDownload@64a5b2bb2d7b23d32c99ccefc799effb357ec69d`
(`plugins/baidu-netdisk/lib/baidu_api.js`, no license declaration found).
These were schema references only; no Rust or GDownload source code was copied.
Only the reference server's transfer request is implemented; no additional task-query
endpoint is used when an exact transfer mapping is absent.

No separate license text was found for the consulted Go reference tree; the
CatPawRunner MIT declaration must not be presented as a license for that entire tree.
This records the actual protocol references, not a completed redistribution audit.
The pan runtime adds no npm or native dependency and does not alter the licensing
of existing components.

Local account files are intentionally plaintext. See `docs/runtime/pan-runtime.md`
for the complete operational and verification boundaries.

## Node.js

The bundled `runtime/node.exe` is distributed under `node-LICENSE.txt`.

## Node Mobile — iOS only

- Source: `https://github.com/nodejs-mobile/nodejs-mobile`, release `v18.20.4`.
- The iOS App embeds the upstream `NodeMobile.xcframework` as a platform runtime,
  not as a standalone executable. No Node core source fork is included.
- License and bundled third-party declarations: `nodejs-mobile-LICENSE.txt`,
  copied from that tag's `LICENSE`; archive and license SHA-256 are pinned in
  `scripts/ios-node-mobile.json` and checked during build preparation.
- This component and its license are not distributed by the Windows packager.

## Undici Web values — MIT, iOS only

- Source: `https://github.com/nodejs/undici`, npm version `6.29.1`, pinned as a
  build-only dependency in the root package lock.
- The iOS build bundles only the Request, Response, Headers and FormData value
  modules and their pure JavaScript closure in `cat-web-values.cjs`. It does not
  bundle Undici's fetch transport, dispatcher, llhttp or WebAssembly parser.
  Network requests continue through the runner's managed Node HTTP transport.
- The original package `LICENSE` is included as `Runtime/licenses/undici-LICENSE.txt`;
  its hash, consumed source inputs and the bundle hash are recorded in the
  iOS native build receipt. Other platforms retain their native Node Web values.

## Native Unicode tables — Unicode License V3

- Source: `libunicode-table.h` in the same QuickJS-NG commit
  `d7215b0ad3fc2c562e8317fd9bca058fd4863804`, included by `libunicode.c`
  and compiled into the shared Node addon and iOS `MeowScriptHost.framework`.
- Copyright © 1991-2026 Unicode, Inc.
- License: `Unicode-3.0.txt` is the complete original copyright and permission
  notice extracted from that table's header, without the C comment delimiters.
  The table and this license are hash-pinned in `scripts/script-host-native.json`;
  source verification also checks that the standalone notice matches the header.
- The Windows license package includes this notice and its manifest entry.
  The iOS App also bundles `Unicode-3.0.txt` as a resource.
  Node's separate ICU declaration does not replace this native-table attribution.

## QuickJS-NG — MIT

- Source: `https://github.com/quickjs-ng/quickjs`, version `0.18.0`, commit
  `d7215b0ad3fc2c562e8317fd9bca058fd4863804`.
- License: `quickjs-ng-LICENSE.txt`, unchanged upstream text and copyrights.
- The common runtime compiles QuickJS as a native library in the Node-API 8
  extension `script_host_native.node`; iOS embeds the same implementation in the
  signed `MeowScriptHost.framework`. Scripts and pure JSON application programs
  compile the same checked-in source and Unicode tables under
  `native/JavaScript/quickjs`, locked by `scripts/script-host-native.json`.
  One library serves independent script and application
  runtimes and native execution threads; no WebAssembly or asm.js engine is shipped.
- `packages/script-host/native/task-context.patch` preserves task identity across
  Promise reactions, native await, generators, finalizers and scheduled jobs. It
  independently retains and restores async-generator execution ownership, so
  queued requests use their own cancellation control without replacing authority.
  The existing cancellation cleanup window is unchanged. It
  does not introduce a script memory quota or change the upstream license.
- `native/JavaScript/patches/regexp-compile-interrupt.patch` polls the owning
  runtime during regular-expression compilation, capture discovery, bytecode
  execution, bidirectional references and global empty replacements. Interrupted
  native operations propagate an uncatchable engine interruption.
- The former BigInt unsigned-truncation and UTF-16 CString patches are covered
  by 0.18's upstream implementations; their behavior regressions remain.
- `packages/script-host/native/arraybuffer-ownership.patch`, applied last,
  makes ArrayBuffer transfer allocate the destination before changing storage
  or detaching its source. The 0.18 realloc callback follows the external owner
  through transfers, preserves its old allocation on failure and releases it
  exactly once. Ordinary inbound buffers retain their owned backing path.
  The upstream license and copyrights remain unchanged.

## OpenSSL — Apache-2.0

- Source: `https://github.com/openssl/openssl`, version `3.5.9`, commit
  `45e844fa2a14ec92d146bd8f5778ac130b6625fb`.
- License: `openssl-LICENSE.txt`, unchanged upstream license text.
- Only libcrypto is linked for script-requested algorithms on native execution
  threads; libssl and the OpenSSL command-line program are not packaged.

## zlib — zlib license

- Source: `https://github.com/madler/zlib`, version `1.3.1`, commit
  `51b7f2abdade71cd9bb0e7a373ef2610ec6f9daf`.
- License: `zlib-LICENSE.txt`, unchanged upstream license text.
- Explicit script compression operations use the native library on the owning
  execution thread. Ordinary client requests do not run these algorithms.

All three native source archives, licenses, the ordered patches, build tools and
artifact locations are pinned in `scripts/script-host-native.json`. Reproduce with
`scripts/quickjs/build-native.mjs`; the build receipt records source/SDK inputs,
ABI, platform, architecture and artifact hashes. The common packager verifies
that inventory without building, fetching or changing the lock.

The historical `quickjs-emscripten-LICENSE.txt` is retained as source history.
It is not a dependency or backend of the common native runtime, its Windows
packaging consumer, or the iOS framework.

## Node compatibility npm dependencies

The controlled Cat Node interface bundles the exact dependency closure recorded in
`package-lock.json`. Each packaged npm directory retains its own original license file.
Its direct JavaScript packages are `@opentelemetry/api` 1.9.0, `acorn` 8.15.0
and `supports-color` 8.1.1. Buffer, events, path, stream and util use Node's
native objects rather than the previous browser compatibility packages.

Acorn parses constant route declarations in the Cat Worker only when a
script does not expose framework route registration. It does not execute source.

- Apache-2.0 package text: `Apache-2.0.txt`.
- MIT package notices and texts: `npm-node-compat-MIT.txt`.
- BSD-3-Clause package text: `npm-node-compat-BSD-3-Clause.txt`.
- ISC package text: `npm-node-compat-ISC.txt`.

## Portable source runtime npm dependencies

The runtime payload preserves each package's original `package.json` and license
file. These packages are used as unmodified libraries; no source code from them is
copied into the application's protocol adapters.

- `acorn` 8.19.0 — MIT, used by `@meowfilm/source-runtime`; the Cat bridge keeps
  its separate 8.15.0 dependency listed above.
- `fontoxpath` 3.34.0 — MIT.
- `linkedom` 0.18.14 — ISC.
- `iconv-lite` 0.6.3 — MIT.
- `buffer` 6.0.3 — MIT, only for the pure-JavaScript encoding/URL SDK, not Cat.
- `string_decoder` 1.3.0 — MIT, Copyright Node.js contributors; source:
  `https://github.com/nodejs/string_decoder`. Its dependency `safe-buffer` 5.2.1
  is MIT, Copyright (c) Feross Aboukhadijeh; source:
  `https://github.com/feross/safe-buffer`. These unmodified pure-JavaScript
  packages support iconv-lite decoding inside the source SDK, using npm `buffer`
  rather than Node builtins. Exact versions and registry integrity are recorded
  in `package-lock.json`. License texts are already included in
  `npm-node-compat-MIT.txt`; the common payload also preserves both packages'
  original `LICENSE` files.
- `whatwg-url` 14.2.0 and `tr46` 5.1.1 — MIT, Copyright (c) Sebastian Mayr;
  `webidl-conversions` 7.0.0 — BSD-2-Clause, Copyright (c) 2014 Domenic Denicola;
  `punycode` 2.3.1 — MIT. Their original license texts remain in the packaged
  dependency directories and apply to their code bundled into `source-sdk.js`.
- Transitive closure: `@acemir/cssom` 0.9.32 (MIT), `boolbase` 2.0.0 (ISC),
  `css-select` 7.0.0 (BSD-2-Clause), `css-what` 8.0.0 (BSD-2-Clause),
  `dom-serializer` 3.1.1 and 2.0.0 (MIT), `domelementtype` 3.0.0 and
  2.3.0 (BSD-2-Clause), `domhandler` 6.0.1 and 5.0.3 (BSD-2-Clause),
  `domutils` 4.0.2 and 3.2.2 (BSD-2-Clause), `entities` 8.1.0, 7.0.1,
  and 4.5.0 (BSD-2-Clause), `html-escaper` 3.0.3 (MIT),
  `htmlparser2` 10.1.0 (MIT), `nth-check` 3.0.1 (BSD-2-Clause),
  `prsc` 4.0.0 (MIT), `safer-buffer` 2.1.2 (MIT), `uhyphen` 0.2.0 (ISC),
  `whynot` 5.0.0 (MIT), and `xspattern` 3.1.0 (MIT).

`node-gyp` 11.5.0 (MIT) and `esbuild` 0.28.1 (MIT) are locked build tools.
They and their executable/development dependencies are not in the runtime payload.
The neutral SDK bundle keeps upstream legal comments and the payload retains
the original dependency license files.

## NetEase token provider and official Watchman SDK

`@meowfilm/netease-token` 0.1.0 is application source under GPL-3.0-only, with
the api-enhanced MIT protocol attribution above. It uses `jsdom` 24.1.3 to load
the official `https://acstatic-dun.126.net/tool.min.js` entry and the Watchman
resources it loads, on demand for token acquisition. Account cookies, account
lifecycle and subscription writes remain native-client responsibilities.

The remote SDK is trusted executable upstream code, not a bundled npm dependency
or vendored SDK copy. Its execution and network behavior are an explicitly
accepted trust boundary; a Worker and jsdom do not make it a security sandbox.
The SDK and its service remain subject to their rights holders' terms. Neither
the api-enhanced MIT license, jsdom's MIT license nor the application's GPL
grants a license to redistribute that SDK. No SDK redistribution permission is
asserted by this notice.

### jsdom production dependencies

The root `package-lock.json` records the exact versions, registry URLs and
integrities. Its installed token-provider closure contains 63 npm package
instances, including nested `rrweb-cssom` 0.8.0 and `entities` 6.0.1 rather than
substituting the versions used by other workspaces. Declared licenses are:

- MIT: `jsdom` 24.1.3 (Copyright (c) 2010 Elijah Insua) and 55 other package
  instances in this closure. Their original notices identify their respective
  copyright holders.
- MIT-0: `@csstools/color-helpers` 5.1.0, Copyright CSSTools Contributors.
- ISC: `lru-cache` 10.4.3 and `saxes` 6.0.0.
- BSD-2-Clause: `entities` 6.0.1 under `parse5` and `webidl-conversions` 7.0.0.
- BSD-3-Clause: `tough-cookie` 4.1.4, Copyright (c) 2015, Salesforce.com, Inc.
- Apache-2.0: `xml-name-validator` 5.0.0.

The common runtime packager preserves the installed package manifests and
original license files byte-for-byte, including `LICENSE.txt`, `LICENSE.md`,
`LICENSE-MIT.txt`, `LICENCE.md`, `License` and `license.md`. Those files contain
the complete license texts for 62 of the 63 package instances and remain in
both platform runtime payloads; no duplicate collection of those texts is added.

The `saxes` 6.0.0 npm package omits its license file. Its upstream
[`v6.0.0/LICENSE`](https://github.com/lddubeau/saxes/blob/v6.0.0/LICENSE), retrieved
on 2026-11-10, grants ISC terms with Copyright (c) Contributors and retains sax's
Copyright (c) Isaac Z. Schlueter and Contributors. The existing
`npm-node-compat-ISC.txt` preserves both attributions with the shared ISC terms.
Distributions must retain it together with this notice and `api-enhanced-MIT.txt`.

The optional `canvas`, `bufferutil` and `utf-8-validate` peers are not installed
or part of this closure. No native addon, canvas or WebAssembly dependency is
authorized by these license declarations.

## QR Code generator — MIT

Copyright (c) 2018 Manuel Bleichenbacher. License: `qr-code-generator-LICENSE.txt`.

## Entry artwork color-selection references — Windows

The Windows music entry palette uses the muted/light-muted color-role approach
described by AndroidX Palette (Copyright 2018 The Android Open Source Project,
Apache-2.0; license text: `Apache-2.0.txt`):
[Palette.java](https://github.com/androidx/androidx/blob/a6a6dfbfb81d34881205521e9e5cb886d91e5011/palette/palette/src/main/java/androidx/palette/graphics/Palette.java)
and [Target.java](https://github.com/androidx/androidx/blob/a6a6dfbfb81d34881205521e9e5cb886d91e5011/palette/palette/src/main/java/androidx/palette/graphics/Target.java).
The application implements its own bounded, alpha-weighted RGB histogram and
population/lightness/chroma scoring in `MusicEntryPalette.cs`; it does not ship
AndroidX, node-vibrant, their quantizers, skin-tone filters or generated swatch
fallbacks. Tone adjustment uses the standard
[Oklab color-space transforms](https://bottosson.github.io/posts/oklab/).
No additional runtime dependency is used.

## Music platform brand marks

The Windows music-platform assets identify NetEase Cloud Music, QQ Music, KuGou
Music, Kuwo Music and Migu Music with marks obtained from image URLs referenced
by their official homepages on 2026-10-29 (NetEase, QQ and KuGou) and 2026-10-30
(Kuwo and Migu). These marks and trademarks remain the property of their respective
owners; they are not covered by the application's GPL license or the API-code
licenses above. Their presence does not imply endorsement or a verified grant
of redistribution or commercial-use rights.

Assets are embedded under
`apps/windows/src/MeowFilm.Capabilities/Accounts/Presentation/Assets/` and loaded
locally, without contacting the platform when the account cards are rendered.
No brand shape was redrawn.

| Asset | Official source | Local transformation | Source SHA-256 |
| --- | --- | --- | --- |
| `netease-music.png` | [NetEase Cloud Music homepage image metadata](https://p3.music.126.net/9z9CeujRSPOPm7Rq2DFw_g==/6674035581283071.jpg?param=200y200) | JPEG to PNG, original 200 × 200 pixels | `186e4684e688681c8b564bcd4b956c9b946d5e960d7ad788199b1b27c56fa19d` |
| `qq-music.png` | [QQ Music homepage logo](https://y.qq.com/mediastyle/yqq/img/logo.png?max_age=2592000) | Cropped the 46 × 46 left-hand mark, excluding the wordmark | `57ee90c7d8d67e9012c7882ce23871af2a654296e8ad567dfb2dc1e0e9d0116f` |
| `kugou-music.png` | [KuGou Music homepage logo](https://www.kugou.com/common/images/icon_logo_v20.png) | Cropped the 76 × 76 left-hand mark and composited its transparent portions over the official favicon blue `#1E84E4` | `1ba3beeb8060dfd9047c27021d63ba5a0d25ccc990ea7f48abe1e7fa62df2d1a` |
| `kuwo-music.png` | [Kuwo Music homepage logo](https://h5s.kuwo.cn/www/kw-www/img/logo.ce08bf7.png) | Cropped the 112 × 112 left-hand mark from the 420 × 120 PNG: `(0, 4, 112, 116)` | `a984eea4477b7092e8235805716a8e9c1a3e410ec6e4c70bb325c49bf78fbdd5` |
| `migu-music.png` | [Migu Music homepage header logo](https://music.migu.cn/v5/static/png/logo-517fd150.png) | Cropped the 64 × 64 left-hand mark from the 290 × 64 PNG: `(0, 0, 64, 64)` | `517fd150cb4443576343d420eea3fc8f7b3e208ef15ec2e50420fbbc68c11010` |

KuGou color reference: [official favicon](https://www.kugou.com/root/favicon.ico),
SHA-256 `78339bbc8393aea26d56c4285488b17a16d60ddc4a698e6b2cfc5d753c91ef24`.

Kuwo and Migu provenance, retrieved on 2026-10-30:

- The [Kuwo homepage](https://www.kuwo.cn/) directly references the Kuwo PNG above
  in its header `<img class="logo">`.
- The [Migu homepage](https://music.migu.cn/v5/) references its
  [official JavaScript module](https://music.migu.cn/v5/static/js/main-4f72ecaf.js),
  which resolves `../png/logo-517fd150.png` relative to the module URL for the
  `migu-logo` header image.
- Both source downloads returned PNG images. Source SHA-256 values above refer
  to the downloaded bytes, before cropping. With Pillow, each image was decoded
  to RGBA, cropped using the stated `(left, top, right, bottom)` coordinates
  (right and bottom exclusive), and saved losslessly as PNG. Original transparent
  pixels, rounded brand backgrounds and intrinsic colors were retained; no
  resizing, recoloring, SVG rendering or redrawing was performed.

| Cropped asset | Output SHA-256 |
| --- | --- |
| `kuwo-music.png` | `488d72ecf0709df33580ae81b5224eecc464419022a506bef8e84405920e7582` |
| `migu-music.png` | `f519240351d82cf337fffa6534c759dfa2921dad3923c33b4cd3f6a50d8825d4` |

## Music platform brand assets — iOS only

The assets below identify music platforms on the iOS account settings page.
Their names, marks and artwork remain the property of their respective rights
holders; use here does not imply affiliation or endorsement. They are not
licensed under this application's GPL source license. Public availability is
not a redistribution license; no open-source or redistribution permission has
been established, and rights must be confirmed before distributing these assets.

- NetEase Cloud Music: `apps/ios/Assets.xcassets/MusicPlatformNetease.imageset/netease.png`,
  unmodified 200×200 PNG from the logo declared by `https://music.163.com/`:
  `https://p3.music.126.net/9z9CeujRSPOPm7Rq2DFw_g==/6674035581283071.jpg?param=200y200`.
- QQ Music: `apps/ios/Assets.xcassets/MusicPlatformQQ.imageset/qq.svg`,
  unmodified SVG referenced by `https://i2.y.qq.com/n3/other/pages/myqq/index.html`:
  `https://y.qq.com/mediastyle/mod/mobile/img/logo.svg?max_age=2592000`.
- Kugou Music: `apps/ios/Assets.xcassets/MusicPlatformKugou.imageset/kugou.png`,
  the original 160×160 icon cropped from the 600×160 official logo referenced
  by `https://m.kugou.com/`:
  `https://m.kugou.com/v3/static/images/img/icon_kugou2.png`.
  No resizing, recolouring or redrawing was applied.

## Windows music audio kernel — NAudio and FFmpeg

- `NAudio.Core` and `NAudio.Wasapi` 3.3.0: Copyright 2008–2026 Mark Heath,
  MIT. Upstream tag `v3.3.0`, revision
  `4f8889e2bd34c3978a25652c6c172fa1502ccba3`:
  <https://github.com/naudio/NAudio/tree/4f8889e2bd34c3978a25652c6c172fa1502ccba3>.
  The license text is reproduced in `NAudio-MIT.txt` (SHA-256
  `8e5bf44655c5acb5cd41fa0c1ff70d4468b7aa54cff192921481d4eac2bb7b58`).
  `NAudio.Effects` is part of Core, not an additional package.
- `FFmpeg.AutoGen` 9.0.1.1: Copyright (c) 2025 Ruslan Balanukhin
  (Rationale One), MIT. Upstream revision
  `7ec3390be7216bd1f8482b824f90c27cb601cd89`:
  <https://github.com/Ruslan-B/FFmpeg.AutoGen/tree/7ec3390be7216bd1f8482b824f90c27cb601cd89>.
  The original license is reproduced in `FFmpeg.AutoGen-MIT.txt` (SHA-256
  `2c450b77bf632b5b3ba04674324caa28ae322e02773c09af71514343ba32dea2`).
  This is a managed C API binding; it does not supply FFmpeg binaries.
- Core's `System.Numerics.Tensors` 9.0.0 dependency: Copyright (c)
  .NET Foundation and Contributors, MIT. The package identifies dotnet/runtime
  revision `9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3`:
  <https://github.com/dotnet/runtime/tree/9d5a6a9aa463d6d10b0b0ba6d5982cc82f363dc3>.
  Its license is reproduced in `dotnet-runtime-MIT.txt` (SHA-256
  `cfc21f5e8bd655ae997eec916138b707b1d290b83272c02a95c9f821b8c87310`).

The independent audio FFmpeg 9.0.1 libraries are built from the unmodified
official release archive:
<https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz>, SHA-256
`cf38e0e28c7e5605942c4a77755349b0145804a397af37eb1fb4c77cb237f635`.
Upstream tag `n9.0.1` resolves to
`bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa`:
<https://github.com/FFmpeg/FFmpeg/tree/bf1b838f2ab88b4f8fd83443325c782ea0e0f7fa>.
The archive's detached signature was verified against the FFmpeg release key
published at <https://ffmpeg.org/ffmpeg-devel.asc>, fingerprint
`FCF986EA15E6E293A5644F10B4322F04D67658D8`; this records cryptographic
verification against that published key, not an independent identity audit.

The locked recipe is `apps/windows/native/AudioKernel/audio-kernel.lock.json`
with the preparation/build scripts `scripts/windows/*audio-kernel*`.
It produces shared Windows x64 `avutil-61.dll`, `swresample-7.dll`,
`avcodec-63.dll`, `avformat-63.dll` and `avfilter-12.dll`, with audio
decoders/demuxers and the bounded audio-filter selection. It disables GPL,
version3 and nonfree components, encoders, muxers, video decoders, network
protocols and device libraries. The only built-in URL protocol is local `file`;
authorized network streams enter through the application's managed AVIO.
The resulting libraries report **LGPL version 2.1 or later**.
The unchanged release licensing overview and LGPL text are already reproduced
in `FFmpeg-LICENSE.md` and `LGPL-2.1-only.txt`.
`FFmpeg-audio-NOTICES.txt` preserves the compatible MIT/ISC/BSD notices in
FFmpeg's common utilities, Ogg header parsing and x86 assembly support, together
with the conditional Boost-licensed erf fallback notice. Ogg's header parsers
do not enable the corresponding video decoders.
No FFmpeg executable, external codec library, libxml2, VLC plugin or private
video FFmpeg archive is part of this audio closure.

The source-build toolchain is MinGW GCC 13.2.0 with MinGW-w64 11.0.1.
Its static libgcc and runtime terms/notices are the existing
`GPL-3.0-only.txt`, `GCC-Runtime-Library-Exception-3.1.txt` and
`MinGW-w64-runtime-NOTICE.txt`, as described below; the “or later” grants
are not narrowed by these filenames. FFmpeg uses native Win32 threads;
the GCC support closure statically links winpthreads, whose terms are
reproduced in the existing `winpthreads-LICENSE.txt`.
The receipt records actual compiler/support-library hashes and audited PE
imports; an unshipped non-system DLL dependency is a build failure.

Each application build packages the complete audio DLL closure and its
`audio-kernel/win-x64/build-receipt.json`, including source identity,
configuration, exact ABI versions, toolchain inputs and DLL SHA-256 values.
Managed identity pins are generated by the same source-build recipe.
Redistributors must fulfill the applicable corresponding-source and
relinking/rebuilding obligations, including these scripts and the identified
FFmpeg sources; a hash-pinned release is not a waiver of those obligations.
The source and generated managed pins can be rebuilt together.

## Apple music audio kernel — FFmpeg

`native/AudioKernel` uses the same unmodified FFmpeg 9.0.1 official archive,
tag, commit, archive hash and release-signature provenance recorded in the
audio entry above. The independent Apple recipe is pinned in
`scripts/ios-audio-kernel.json` and built by
`scripts/prepare-ios-audio-kernel.py`; it does not use the Windows bindings,
DLL recipe, NAudio or the video kernel's FFmpeg source.

The LGPL 2.1-or-later audio closure contains libavutil, libswresample,
libavcodec and libavformat. GPL, version3, nonfree, programs, video decoders,
avfilter and swscale are disabled. HTTP/TCP/crypto are included solely for
the application's exact scoped HLS loopback capability; ordinary upstream
input is managed custom AVIO. The C adapter is original project code, not a
copy of KSPlayer or another player. Static audio libraries are linked inside
the dynamic `CMeowAudioKernel.framework`, with two-level namespace and only
the seven `meow_audio_decoder_*` exports. FFmpeg symbols are private to that
framework and do not interpose on the existing video FFmpeg 8.1.2.

Apple build inputs use the selected Xcode SDK and Clang toolchain. The App
embeds this framework and includes the existing `FFmpeg-LICENSE.md`,
`LGPL-2.1-only.txt` and `FFmpeg-audio-NOTICES.txt`; no duplicate license text
is created. The source, target settings and exact build/verification recipe
are available in this repository and the identified upstream archive.
Redistributors must provide the applicable corresponding source and allow
rebuilding/relinking or replacing the LGPL component, including the
application source and adapter/build recipe needed for that process.
Dynamic encapsulation, code signing and iOS installation restrictions do not
waive those obligations; a signed App or a prepared XCFramework alone is not
a claim of redistribution compliance.

## Video playback kernel — FFmpeg and libxml2

FFmpeg 8.1.2 is built from the pinned upstream revision
`38b88335f99e76ed89ff3c93f877fdefce736c13`:
<https://github.com/FFmpeg/FFmpeg/tree/38b88335f99e76ed89ff3c93f877fdefce736c13>.
The source archive SHA-256 is
`2ae7e42343cfffb811d15cfe98b6d005f082595fcdf034d30a4ff90cfed9f9c6`.
The upstream licensing overview is reproduced in `FFmpeg-LICENSE.md`; the
LGPL texts are `LGPL-2.1-only.txt` and `LGPL-3.0-only.txt`. These filenames
identify the texts, not a removal of upstream's “or later” grant.

- iOS (`native/VideoKernel`) is a self-owned Swift + C bridge playback kernel
  under GPL-3.0-only (`GPL-3.0-only.txt`) linking static FFmpeg 8.1.2
  (`avformat`, `avcodec`, `avutil`, `swresample`, `swscale`), built with
  `--enable-gpl --disable-nonfree --enable-version3` (GPLv3 or later).
  The exact recipe and source adaptations are in `scripts/ios-video-kernel.json`
  and `native/VideoKernel/patches/ffmpeg-native-output.patch`.
- This software is based in part on the work of the Independent JPEG Group.
  The upstream IJG notices and the unchanged-source statement are reproduced
  in `FFmpeg-IJG-NOTICE.txt` for the iOS decoding build.

Windows also builds libxml2 2.14.6 from
<https://download.gnome.org/sources/libxml2/2.14/libxml2-2.14.6.tar.xz>,
SHA-256 `7ce458a0affeb83f0b55f1f4f9e0e55735dbfc1a9de124ee86fb4a66b597203a`.
Copyright (C) 1998–2012 Daniel Veillard and the libxml2 contributors; upstream
MIT-style text is reproduced in `libxml2-MIT.txt`. Its `dict.c` also credits
Copyright (C) 2003–2012 Daniel Veillard; `list.c` credits Copyright (C) 2000 Gary
Pennington and Daniel Veillard. Their distinct permission/disclaimer is appended
to `libxml2-MIT.txt` without dropping those notices.
iOS links the Apple SDK's system libxml2 rather than shipping a second copy.

## Windows music toolchain — GCC, MinGW-w64 and winpthreads

The Windows music audio kernel links the GCC 13.2.0 support library `libgcc`
statically. Its source is GCC tag `releases/gcc-13.2.0`:
<https://github.com/gcc-mirror/gcc/tree/releases/gcc-13.2.0/libgcc>.
Copyright (C) Free Software Foundation, Inc. The applicable grant is
GPL-3.0-or-later WITH GCC-exception-3.1. The GPLv3 terms are supplied by the
existing `GPL-3.0-only.txt`; that filename does not narrow libgcc's “or later”
grant. `GCC-Runtime-Library-Exception-3.1.txt` reproduces upstream
`COPYING.RUNTIME` verbatim (SHA-256
`9d6b43ce4d8de0c878bf16b54d8e7a10d9bd42b75178153e3af6a815bdc90f74`).
The exception applies to eligible compilation processes as defined in that
text; it does not change FFmpeg's separate license obligations.

That Windows music toolchain also contributes the MinGW-w64 runtime and statically
linked winpthreads. Installed headers identify MinGW-w64 11.0.1; upstream tag
`v11.0.1` supplies the source notices:
<https://github.com/mingw-w64/mingw-w64/tree/v11.0.1>.
`MinGW-w64-runtime-NOTICE.txt` preserves the upstream runtime notice verbatim.
`winpthreads-LICENSE.txt` contains its MIT grant, the derived Lockless Inc.
BSD-style notice, and the additional 2011–2016 notice from `src/misc.c`.
The native build receipt records the actual static archive identities; the
upstream attribution alone does not establish absence of toolchain patches.
These Windows support libraries are not included in the iOS kernel.

## AetherEngine — Apple video kernel

`native/AetherKernel` prepares AetherEngine 7.68.0 at
[`0de31d0b9478b99dd49baa2bd8318604a49f99a3`](https://github.com/superuser404notfound/AetherEngine/tree/0de31d0b9478b99dd49baa2bd8318604a49f99a3),
archive SHA-256 `ed70939a9af2e55959998f8f245d2780075ab17446f8bc67b2a5c3c81fab070e`.
Copyright (C) 2026 Vincent Herbst. LGPL version 3 with the upstream App Store /
DRM additional permission. `AetherEngine-EXCEPTION.txt` preserves that permission;
the existing `LGPL-3.0-only.txt` and incorporated `GPL-3.0-only.txt` supply the
license bodies without a second copy. The permission does not waive source
obligations or grant an exception for dependencies owned by other authors.
Reviewed local source changes are recorded in `scripts/ios-aether-kernel.json`.
The `runtime-host.patch` keeps application lifecycle, audio activation and
system media commands with the host, forwards explicit native PiP transport
intent, waits for real reader/output teardown, and disables upstream diagnostic
delivery. It does not add DASH template/list support or encrypted-media decoding.

The pinned FFmpegBuild 3.15.0 package is
[`d445e6daa180db73f12d47606a00c914f682e278`](https://github.com/superuser404notfound/FFmpegBuild/tree/d445e6daa180db73f12d47606a00c914f682e278),
archive SHA-256 `a5a2a03e39d938fa900789c65ab4c1c2b8f3001d0bc82c557cbb74455df2eec1`.
Its build record identifies FFmpeg 8.1.3
`1041abdc962f4cc4f394aa8de9dc5236c0c3b9e7` with published local patches,
dav1d 1.5.4 `54706fc6bc0cdecab7e9593974a4039cc038fca7`, zimg 3.0.6
`f819b14e8f39d1282400b0d9543e8ef73c1b2bbd`, and libzvbi 0.2.46
`34bb6f660c5c1bacd4a66cc7b22311e4ca3c1ab5` with its published LGPL stub
adaptation. Nine dynamic AetherLib frameworks are consumed, separate from the
static `native/VideoKernel` FFmpeg 8.1.2 build. Prefixes alone do not isolate C symbols: Aether source and its
FFmpeg references stay in the dynamic `MeowAetherKernel` wrapper. App linkage
checks must verify the actual imported symbol libraries and binary closure.

LibDovi 2.2.0 is
[`caff700968700467c089a3f38c8b4180620518b5`](https://github.com/superuser404notfound/LibDovi/tree/caff700968700467c089a3f38c8b4180620518b5),
archive SHA-256 `84af1b63a2af380bab3390e584cb4a44dfcc93b3d17b91478c1171a049033901`.
Its record identifies dolby_vision/libdovi 3.4.0 from dovi_tool
`d1abe0e27ff2c7ab3339614d06db9f8a058af6b2`; the MIT alternative is selected.
It is static only inside the wrapper. SMB, LibTrueHD and optional FEL/object
audio traits are not enabled by this package.

`Aether-THIRD-PARTY-NOTICES.txt`, `Aether-libzvbi-ure-MIT.txt`,
`WTFPL-2.0.txt` and `dav1d-BSD-2-Clause.txt` preserve the additional notices,
while reusing the existing GNU/MIT bodies in `LGPL-2.1-only.txt` and
`libxml2-MIT.txt`. The iOS resource list includes those shared texts without
coupling to Windows `FilmKernel-THIRD-PARTY-NOTICES.txt`.
The upstream framework metadata correction, precise inputs and accepted
device/simulator/macOS slices are documented in `native/AetherKernel/README.md`.
Prepared binaries and metadata checks are not a reproduced source build,
verified playback result or a declaration of redistribution compliance.

## Windows film playback kernel — independent native C/C++ implementation

`apps/windows/native/FilmKernel/` is the movie-only native kernel (C ABI v3)
under GPL-3.0-only (`GPL-3.0-only.txt`). Historically informed by KSPlayer
(`d0d063ab1f045735129b6617591ad2148d88d5db`, GPLv3), it is now an independent
Windows C/C++ implementation with D3D11 + `libplacebo` adaptive HDR/SDR output,
WASAPI clock/hot-plug/wall-clock fallback, libass subtitle rendering and DOVI
composition. It does not link libVLC, libmpv or the independent Windows music
playback owner.

The authoritative 17-source inventory, exact archive URLs/SHA-256, upstream
license-file SHA-256, source-tree identities and checked local build inputs are
in `apps/windows/native/FilmKernel/film-kernel.lock.json`. The following source
revisions are fixed; a tag below is not permission to substitute a newer tag.

| Source | Fixed version/revision and upstream | License and use |
| --- | --- | --- |
| FFmpeg | [8.1.2, `38b88335f99e76ed89ff3c93f877fdefce736c13`](https://github.com/FFmpeg/FFmpeg/tree/38b88335f99e76ed89ff3c93f877fdefce736c13) | LGPL-2.1-or-later; avformat/avcodec/avutil/avfilter/swresample/swscale |
| libplacebo | [7.387.0, `v7.387.0`](https://github.com/haasn/libplacebo/tree/v7.387.0) | LGPL-2.1-or-later; D3D11 shaders, HDR color mapping, DOVI reshape and enhancement-layer composition |
| libass | [0.17.4](https://github.com/libass/libass/tree/0.17.4) | ISC; subtitle rendering with the Windows DirectWrite font provider and libunibreak CJK line-breaking |
| libunibreak | [6.1, `libunibreak_6_1`](https://github.com/adah1972/libunibreak/releases/tag/libunibreak_6_1) | zlib; Unicode line-breaking for libass CJK subtitles (`zlib-LICENSE.txt`, copyrights in `FilmKernel-THIRD-PARTY-NOTICES.txt`) |
| FreeType | [2.14.1, `VER-2-14-1`](https://github.com/freetype/freetype/tree/VER-2-14-1) | GPL-2.0-or-later alternative selected for this GPLv3 derivative; separate contributed-file notices retained |
| FriBidi | [1.0.16, `v1.0.16`](https://github.com/fribidi/fribidi/tree/v1.0.16) | LGPL-2.1-or-later; bidirectional text and shaping |
| HarfBuzz | [11.5.1](https://github.com/harfbuzz/harfbuzz/tree/11.5.1) | Old MIT; text shaping, with the separate Microsoft USE notice |
| dav1d | [1.5.3](https://github.com/videolan/dav1d/tree/1.5.3) | BSD-2-Clause; AV1 software decoding |
| glslang | [15.1.0](https://github.com/KhronosGroup/glslang/tree/15.1.0) | Upstream mixed BSD/MIT/Apache/GPL-with-Bison-exception and NVIDIA notices; runtime shader compilation |
| SPIRV-Cross | [1.4.309.0, `vulkan-sdk-1.4.309.0`](https://github.com/KhronosGroup/SPIRV-Cross/tree/vulkan-sdk-1.4.309.0) | Apache-2.0 alternative selected for library code; Khronos SPIR-V header notice retained; shader translation |
| fast_float | [`97b54ca9e75f5303507699d27c6b4f4efe4641a1`](https://github.com/fastfloat/fast_float/tree/97b54ca9e75f5303507699d27c6b4f4efe4641a1) | Apache-2.0 alternative selected; header dependency of libplacebo |
| libxml2 | [2.14.6, `v2.14.6`](https://github.com/GNOME/libxml2/tree/v2.14.6) | MIT-style; DASH XML parsing; existing `libxml2-MIT.txt` also preserves dict/list notices |
| zlib | [1.3.1, `v1.3.1`](https://github.com/madler/zlib/tree/v1.3.1) | zlib; existing `zlib-LICENSE.txt` is the exact locked text |
| bzip2 | [1.0.8](https://sourceware.org/pub/bzip2/bzip2-1.0.8.tar.gz) | bzip2 license; libbzip2 compression support |
| XZ Utils | [5.8.1, `v5.8.1`](https://github.com/tukaani-project/xz/tree/v5.8.1) | 0BSD for liblzma; command-line tools, scripts and their GPL/LGPL components are not shipped by this kernel |
| Jinja | [`15206881c006c79667fe5154fe80c01c65410679`](https://github.com/pallets/jinja/tree/15206881c006c79667fe5154fe80c01c65410679) | BSD-3-Clause; libplacebo build-time template generator, not a runtime Python dependency |
| MarkupSafe | [`297fc8e356e6836a62087949245d09a28e9f1b13`](https://github.com/pallets/markupsafe/tree/297fc8e356e6836a62087949245d09a28e9f1b13) | BSD-3-Clause; Jinja build dependency, not shipped Python code |

The native recipe statically links the enabled libraries into
`meowfilm_film_kernel.dll`. FFmpeg disables GPL/nonfree components, its own
network/protocols, encoders, muxers, programs and avdevice; it enables decoders,
demuxers, parsers, bitstream filters, D3D11VA and the aformat/aresample/atempo
audio filters. The libplacebo build enables D3D11, DOVI and glslang, but not
libdovi, shaderc, Vulkan, OpenGL, demos or tests. The source and build
adaptations are identified by the lock and the adjacent patches, including
controlled AVIO/ClearKey handling, DOVI fixes and native GNU make response-file
generation for Windows static archives. Source configuration is not
proof of the final DLL's imports or static-link closure.

`film_dovi.cpp` adapts only the NAL-63 enhancement-layer extraction mechanism
from FFmpeg master at **`0834db516348d253b2c51f969188501d324c698d`**:
[libavcodec/bsf/dovi_split.c](https://github.com/FFmpeg/FFmpeg/blob/0834db516348d253b2c51f969188501d324c698d/libavcodec/bsf/dovi_split.c),
upstream source SHA-256
`00da865a8d3dbd6b7d993c0121554201525fe5d4480ac8a39f2ab161a4a1714a`,
LGPL-2.1-or-later, FFmpeg contributors. The adjacent `ffmpeg-dovi.patch` also
backports hvcE enhancement configuration from that revision and corrects RPU
association/parsing in the pinned 8.1.2 decoder. This is not use of master
FFmpeg's BSF ABI, an upgrade of the decoding baseline, or an extraction from
mpv. No mpv source or executable is linked or distributed by this film kernel.

When the active display has Windows HDR enabled, the output uses standard
D3D11/DXGI FP16 scRGB native HDR with DOVI dynamic metadata and required
enhancement-layer samples (`native_hdr = true, tone_mapped = false`). When the
display has HDR disabled or is an SDR monitor, the kernel never force-enables
Windows HDR and instead performs `libplacebo` dynamic Peak Detection and
HDR/DOVI-to-SDR tone-mapping onto an 8-bit SDR swapchain (`native_hdr = false,
tone_mapped = true`). It does not emit proprietary DV/LLDV transport signals.
These are implementation boundaries, not claims of verified Dolby certification,
profile coverage or real display output.

The shared `GPL-3.0-only.txt`, `GPL-2.0-only.txt`, `LGPL-2.1-only.txt`,
`LGPL-3.0-only.txt`, `Apache-2.0.txt`, `FFmpeg-LICENSE.md`,
`FFmpeg-IJG-NOTICE.txt`, `libxml2-MIT.txt` and `zlib-LICENSE.txt` supply the
existing terms without duplicate GNU/Apache bodies. These text filenames do
not remove any upstream “or later” or alternative-license grant.
`FilmKernel-THIRD-PARTY-NOTICES.txt` preserves the additional dependency
copyrights, distinct permission/disclaimer texts and exceptions, and names
the existing shared ISC/MIT/BSD texts it reuses. All locked upstream license
files are checked, including unselected alternatives and build-only sources;
not every alternative requires another copy of its license body.
This software is based in part on the work of the Independent JPEG Group;
the pinned IJG files remain locally unchanged as stated in the shared notice.

The film build driver selects LLVM-MinGW `20261104` (x86_64 UCRT), separately
from the Windows music GCC toolchain. The release's fixed build scripts
identify [LLVM `llvmorg-23.1.5`](https://github.com/llvm/llvm-project/tree/llvmorg-23.1.5)
and [MinGW-w64 `ee9bd87644156c41072a11ad957d1e11831a72df`](https://github.com/mingw-w64/mingw-w64/tree/ee9bd87644156c41072a11ad957d1e11831a72df).
The LLVM exception and legacy NCSA notice are preserved once in the film
third-party notices, with the existing Apache body reused. Existing
`MinGW-w64-runtime-NOTICE.txt` and `winpthreads-LICENSE.txt` retain the shared
runtime grants; the film notices additionally retain Rich Felker's musl
copyright and the 2026 winpthreads misc.c attribution. Actual linked archives,
compiler/runtime identities, per-file exceptions and absence of redistributed
private toolchain DLLs still require the Windows build receipt/link-map audit.
The upstream release identity is not evidence that a local SDK is unmodified.

Static linking does not waive source/relinking obligations. Distribution must
make the corresponding source, exact local changes and build material
available under the applicable GPL/LGPL terms, including required installation
information where applicable. A lock, upstream hyperlink or license manifest
alone is not that delivery. Patent, codec and Dolby trademark/certification
permissions are not supplied by these open-source notices.
`redistributionPermissionVerified` remains false; this notice does not declare
the still-unverified final binary/distribution legally or technically accepted.

## ZXing.Net — Apache-2.0 (Windows)

- NuGet `ZXing.Net` 0.16.11, exact locked package; upstream
  [micjahn/ZXing.Net](https://github.com/micjahn/ZXing.Net) release `v0.16.11.0`
  (`fd58f3c4a5d80ccf45ef1237899a65b0ba5b83a0`).
- Copyright Michael Jahn and ZXing contributors. Windows uses the managed QR
  decoder for replacement-rule image import; no camera or network decoder.
- The NuGet package declares Apache-2.0; upstream `COPYING` contains the same
  license. License text: `Apache-2.0.txt`, already in the Windows license manifest.

## Microsoft Windows App SDK (Windows)

- NuGet `Microsoft.WindowsAppSDK` 1.8.260921001, with exact transitive versions
  and package hashes recorded in the Windows `packages.lock.json` files.
- The Windows presentation uses WinUI 3 and a page-owned `SwapChainPanel`.
  Decoding, playback state and native resource retirement remain application
  responsibilities; no upstream player controls are incorporated.
- `WindowsAppSDK-LICENSE.txt` and `WindowsAppSDK-NOTICE.txt` are the unmodified
  license and notice supplied by that NuGet package. They are included in the
  Windows license manifest. The SDK's redistribution terms apply to the files
  placed with the application by its NuGet build targets; this notice does not
  independently certify the final distribution.

## Microsoft Win2D (Windows)

- NuGet `Microsoft.Graphics.Win2D` 1.4.0 (`Win2d.githash.txt`:
  `57e06e2c24703526f500035129251d063881a44d`), from
  [microsoft/Win2D](https://github.com/microsoft/Win2D), locked in
  `MeowFilm.Desktop` and downstream Windows `packages.lock.json` files.
- Copyright (c) Microsoft Corporation. Used by `SidebarBackdrop` for compositor
  `GaussianBlurEffect` over the dedicated shell background visual surface, and
  by the novel reader for DirectWrite text layout and GPU page surfaces.
- License text: `Win2D-MIT.txt` (upstream `LICENSE.txt`), included in the
  Windows license manifest.
