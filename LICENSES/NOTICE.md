# Third-party notices

The application's own source code is licensed GPL-3.0-only (`GPL-3.0-only.txt`).
Native C# and Swift adaptations of the projects below are combined GPL-3.0-only code;
the adapted original portions keep their original licenses and attributions.

No executable, server, account database, Node/Python/Go runtime or reference script
from the adapted business projects is bundled or executed. Runtime/library dependencies
are listed separately below. Public protocol keys, request-signing
constants and device-profile parameters taken from these sources are client
protocol parameters, not user credentials, and are not an endorsement of their
cryptographic design. No device-registration bypass, anti-bot simulation,
media-unlocking, challenge bypass or credential refresher is copied.

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
- iOS: music interface and behavior in `native/MeowKit/Sources/MeowMusicFeature` and
  `native/MeowKit/Sources/MeowUI`; ported files keep their SPDX and author notices.

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
  `models/{base,search,songlist,user}.py` and the response/versioning envelope;
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
- Lyrics: `module/{search_lyric,lyric}.js` and `util/{request,util}.js`; system ZLibStream
  replaces pako.
- Feedback: the KG send path was audited but is unavailable; the position-contract patch over
  `e19a52a91b9ceb78d9acce768c357b61be9586d5` is not represented.

## LX Music Desktop — Apache-2.0

- Source: `lyswhut/lx-music-desktop` `ad95d5091c9ed689fa72b5e5c849df65f5a679ce`.
- Copyright lyswhut and LX Music contributors. License: `Apache-2.0.txt`.

Adapted portions (Kuwo/Migu public catalog, `MusicPlatformHttp.PublicCatalog.cs` and the
public song metadata mapper):

- `src/renderer/utils/musicSdk/kw/{musicSearch,songList,util,index,lyric}.js`
- `src/renderer/utils/musicSdk/mg/{musicSearch,songList,musicInfo}.js` and `utils/index.js`

## Node.js

The bundled `runtime/node.exe` is distributed under `node-LICENSE.txt`.

## QuickJS / quickjs-emscripten — MIT

- Source: `https://github.com/justjake/quickjs-emscripten`, npm release `0.32.0`;
  registry release gitHead `df4efb9ef2cb25c417ecb57986da462d11b244ed`.
- Bundled packages: `quickjs-emscripten-core`, `@jitl/quickjs-wasmfile-release-sync`
  and its/core's shared transitive dependency `@jitl/quickjs-ffi-types`, all `0.32.0`.
- quickjs-emscripten Copyright (c) 2019-2024 Jake Teton-Landis.
- QuickJS Copyright (c) 2017-2021 Fabrice Bellard and Charlie Gordon.
- License: `quickjs-emscripten-LICENSE.txt`, copied byte-for-byte from the
  release-sync package's combined original LICENSE. Each packaged npm dependency
  also retains its own original LICENSE.

The unmodified release-sync `dist/emscripten-module.wasm` is the only permitted
script-engine binary. Its exact package manifest, registry URL/integrity, license,
length and SHA-256 are pinned in `scripts/quickjs-runtime.json`; no debug/asyncify
engine variant or upstream reference script is bundled.

## Portable source runtime npm dependencies

The runtime payload preserves each package's original `package.json` and license
file. These packages are used as unmodified libraries; no source code from them is
copied into the application's protocol adapters.

- `acorn` 8.19.0 — MIT.
- `fontoxpath` 3.34.0 — MIT.
- `linkedom` 0.18.14 — ISC.
- `iconv-lite` 0.6.3 — MIT.
- Transitive closure: `@acemir/cssom` 0.9.32 (MIT), `boolbase` 2.0.0 (ISC),
  `css-select` 7.0.0 (BSD-2-Clause), `css-what` 8.0.0 (BSD-2-Clause),
  `dom-serializer` 3.1.1 and 2.0.0 (MIT), `domelementtype` 3.0.0 and
  2.3.0 (BSD-2-Clause), `domhandler` 6.0.1 and 5.0.3 (BSD-2-Clause),
  `domutils` 4.0.2 and 3.2.2 (BSD-2-Clause), `entities` 8.1.0, 7.0.1,
  and 4.5.0 (BSD-2-Clause), `html-escaper` 3.0.3 (MIT),
  `htmlparser2` 10.1.0 (MIT), `nth-check` 3.0.1 (BSD-2-Clause),
  `prsc` 4.0.0 (MIT), `safer-buffer` 2.1.2 (MIT), `uhyphen` 0.2.0 (ISC),
  `whynot` 5.0.0 (MIT), and `xspattern` 3.1.0 (MIT).

## QR Code generator — MIT

Copyright (c) 2018 Manuel Bleichenbacher. License: `qr-code-generator-LICENSE.txt`.
