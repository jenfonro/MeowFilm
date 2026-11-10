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
- Platforms: `PlatformDiscovery.cs` translates `static/discovery-normalize.js` and
  remains Apache-2.0. Metadata, content dispatch and personal reads adapt
  `static/netease-client.js`, `src/netease.ts`, `src/qq-library.ts`, `src/qq-music.ts`,
  `src/kugou.ts` and `src/kugou-play.ts`; song identity adapts `static/song-identity.js`;
  chorus metadata (`PlatformChorus.cs`) adapts `static/chorus-marker.js`, `src/kugou-media.ts`
  and `src/qq-music.ts`.
- PlaybackCache: `static/source-cache.js`, `static/audio-file-cache.js` and
  `static/chorus-marker.js`.
- Lyrics: `src/{qq-music,kugou-media,netease}.ts` and
  `static/{netease-client,kugou-lyrics,lyric-timing,player}.js`. `KrcLyrics.cs`
  remains Apache-2.0.
- Library: queue, song identity, quality, history, file-cache policy, lyric timing,
  decoded-KRC translation, display positioning and lyric client/cache policy, from
  `static/{lyric-timing,kugou-lyrics,lyrics,player}.js` and the Netease/QQ/KuGou lyric
  client methods. The timing and decoded-KRC algorithm bodies are unchanged.
- Feedback: `NativeMusicFeedbackProtocol.cs`, `FeedbackProtocolRules.cs` and
  `FeedbackProtocolRequests.cs` adapt the lifecycle guards and WY Weblog mapping from
  `static/playback-feedback.js`, the feedback route in `src/netease.ts`, `src/qq-feedback.ts`
  and `src/kugou-feedback.ts`; `MusicFeedbackProtocolTests.cs` uses synthetic vectors from
  `tests/{netease,qq-feedback,kugou-feedback}.test.cjs`.
- Sync: `MusicSyncAdapter.cs`.
- Windows: `MusicPlaybackIcon.cs` adapts the heart/FM SVGs in `static/index.html`
  and all 42 scene SVGs in `static/fm-icons.js` into frozen native WPF geometry,
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
  is rewritten as a bounded native literal reader without expression evaluation.

## NeteaseCloudMusicApiEnhanced/api-enhanced — MIT

- Audited protocol pin: `19c7dad6db7d660954c7a615a686ff0acb030f91`, selected from upstream
  commit metadata at or before the iWebPlus commit; not an embedded dependency or executable.
- Copyright (c) 2013-2022 Binaryify. License: `api-enhanced-MIT.txt`.

Adapted portions:

- Accounts: `module/login_{status,qr_key,qr_check,qr_create}.js`; SMS via
  `module/{captcha_sent,login_cellphone,login_status}.js`, `util/{option,request}.js` and
  `util/config.json` (captcha branch only; no password login or verification bypass);
  profile/entitlement via `module/vip_info_v2.js`.
- Platforms: `PlatformProtocols.cs` translates the WEAPI/EAPI recipes from `util/crypto.js`,
  checked against `util/request.js` and `util/config.json`, with endpoint fields from the
  corresponding `module/*.js`; `PlatformArtist.cs` uses `module/artists.js`
  (blob `e403fe8512f85565d4113e0fc54584bf4b17f33e`); `PlatformXeapi.cs` translates
  `module/register_xeapikey.js`, `util/xeapiKey.js`, `util/crypto.js` and `util/request.js`
  using system cryptography; `OfficialMusicAudio.cs` adapts `module/song_music_detail.js` and
  `module/song_url_v1.js`; chorus metadata uses `module/song_chorus.js`.
- Lyrics: `module/lyric_new.js`.
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
  SPDX-FileCopyrightText: Copyright (c) 2024 沉默の金 <cmzj@cmzj.org>. `QrcCipher.cs`
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
  `user.get_fav_song`; `song.get_song_urls`; `ClientContexts/MusicQqClientProtocol.cs`
  and `ClientContexts/MusicQqQimeiRecipe.cs` adapt `qqmusic_api/utils/{device,qimei,android_session}.py`,
  `qqmusic_api/core/versioning.py`, `qqmusic_api/models/request.py`,
  `qqmusic_api/modules/{login,user,song}.py` and `tests/test_qimei.py` vectors.
- Lyrics: `modules/lyric.py`, `models/lyric.py`, `algorithms/__init__.py` and
  `algorithms/tripledes.py`.
- Feedback: `FeedbackProtocolQqRequests.cs`, `FeedbackProtocolQqReceipt.cs` and
  `NativeMusicFeedbackQq.cs` adapt `web/src/routes/report.py`, `qqmusic_api/modules/song.py`,
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
  chorus recipe; `ClientContexts/MusicKgClientProtocol.cs` adapts `module/register_dev.js`
  and its fixed crypto/request helpers with the same retained MIT notice.
- Discovery: `Platforms/PlatformKgDiscovery*.cs` and
  `native/MeowKit/Sources/MeowMusicNativeService/Catalog/MusicNativePublicBrowseKugou.swift` adapt
  `module/{top_playlist,top_song,playlist_tags,rank_list,rank_audio}.js` and the fixed
  signing helpers above; the directory, tags and rank-audio recipes remain data-only
  native calls, without a generic upstream module dispatcher.
- Lyrics: `module/{search_lyric,lyric}.js` and `util/{request,util}.js`; system ZLibStream
  replaces pako.
- Feedback: the KG send path was audited but is unavailable; the position-contract patch over
  `e19a52a91b9ceb78d9acce768c357b61be9586d5` is not represented.

## SkiaSharp and Skia — MIT and bundled third-party notices

- Windows WebP artwork decoding uses NuGet `SkiaSharp` and
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

Adapted portions (Kuwo/Migu public catalog, `MusicPlatformHttp.PublicCatalog.cs` and the
public song metadata mapper):

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
  `pan-interceptor.js` and `cat-output.js` were informed by
  `runtimePanCapture.js`, `panmockDetailCodec.js` and `panDisplayMeta.js`.
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
- The consulted `panUc.js` attributes its TV configuration to OpenList
  `drivers/quark_uc_tv`; that secondary source revision and license chain were not
  verified here. No OpenList library or source file is bundled, and this notice
  does not relabel that reference as MIT.
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

Quark TV and UC TV token refresh intentionally uses the reference third-party
`http://api.extscreen.com/{quarkdrive,ucdrive}/token` service. Long-lived credentials
are sent to that service over cleartext HTTP; the official file APIs use HTTPS.
This is an explicitly selected interoperability path, not an endorsement of the
third party or a claim of secure credential transport. Local account files are
also intentionally plaintext. See `docs/runtime/pan-runtime.md` for the complete
operational and verification boundaries.

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

## Native QuickJS-NG — MIT

- Source: https://github.com/quickjs-ng/quickjs, v0.18.0,
  commit `d7215b0ad3fc2c562e8317fd9bca058fd4863804`.
- License: `quickjs-native-MIT.txt`; original copyright notices remain in the
  engine sources under `native/JavaScript/quickjs`.
- Local changes add interrupt polling during RegExp compilation (including
  capture pre-scans), execution opcode dispatch, forward/backward references,
  assertion/backtracking stack loops and the global empty-replacement loop.
  Interruption is propagated as uncatchable engine errors. The reproducible patch
  is `native/JavaScript/patches/regexp-compile-interrupt.patch`.
- Upstream, patched-source, patch and license hashes: `scripts/quickjs-native.json`.
- Windows links the engine into `meowfilm_js.dll` through the application's
  JSON-only C adapter; iOS statically links the same source through the local
  `MeowJavaScript` Swift package and bundles `quickjs-native-MIT.txt`.
  The upstream command-line interpreter, libc/I/O module
  and module loader are not included. This client-side engine is independent
  of the Node script host and does not replace its native script-execution engine.

### Native Unicode tables — Unicode License V3

- Source: `libunicode-table.h` in the same QuickJS-NG commit
  `d7215b0ad3fc2c562e8317fd9bca058fd4863804`, included by `libunicode.c`
  and compiled into the Windows `meowfilm_js.dll` and the iOS static engine.
- Copyright © 1991-2026 Unicode, Inc.
- License: `Unicode-3.0.txt` is the complete original copyright and permission
  notice extracted from that table's header, without the C comment delimiters.
  The table and this license are hash-pinned in `scripts/quickjs-native.json`;
  source verification also checks that the standalone notice matches the header.
- The Windows license package includes this notice and its manifest entry.
  The iOS App also bundles `Unicode-3.0.txt` as a resource.
  Node's separate ICU declaration does not replace this native-table attribution.

## QuickJS-NG — MIT

- Source: `https://github.com/quickjs-ng/quickjs`, version `0.14.0`, commit
  `3c051980ab7e783dfbfb1c70c014ce5e05ecf24c`.
- License: `quickjs-ng-LICENSE.txt`, unchanged upstream text and copyrights.
- The common runtime compiles QuickJS as a native library in the Node-API 8
  extension `script_host_native.node`; iOS embeds the same implementation in the
  signed `MeowScriptHost.framework`, separate from the client's rule engine.
  One library serves independent script
  runtimes and native execution threads; no WebAssembly or asm.js engine is shipped.
- `packages/script-host/native/task-context.patch` preserves task identity across
  Promise reactions, native await, generators, finalizers and scheduled jobs. It
  independently retains and restores async-generator execution ownership, so
  queued requests use their own cancellation control without replacing authority.
  The existing cancellation cleanup window is unchanged. It
  does not introduce a script memory quota or change the upstream license.
- `packages/script-host/native/bigint-uintn.patch`, applied after that patch,
  corrects the upstream `BigInt.asUintN` unsigned truncation and sign extension
  for short and multi-limb integers, including limb-width boundaries. It preserves
  coercion order, `BigInt.asIntN` semantics and the existing allocation boundary;
  the upstream license and copyrights remain unchanged.
- `packages/script-host/native/utf16-cstring.patch`, applied after the two patches
  above, corrects upstream UTF-16 CString ownership for non-normal wide strings.
  Slices and indirect storage are copied into an owned normal string before
  returning data to the paired free API; normal wide strings retain their
  zero-copy path. The upstream license and copyrights remain unchanged.
- `packages/script-host/native/arraybuffer-ownership.patch`, applied last,
  corrects ArrayBuffer detach and transfer ownership for externally backed
  buffers. Detaching consumes the release callback once; transfers preserve
  bytes without reallocating externally owned memory and detach the original
  only after successful allocation. Ordinary inbound buffers keep their existing
  backing path. The upstream license and copyrights remain unchanged.

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

The historical `quickjs-emscripten-LICENSE.txt` is retained for platform consumers
that have not yet migrated. It is not a dependency or backend of the new common
runtime or its iOS framework. The Windows packaging consumer is not migrated by
the iOS adaptation, so its remaining attribution is retained.

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
