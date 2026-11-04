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
  `native/MeowKit/Sources/MeowUI`; queue appearance and drag admission follow
  `static/queue-sheet.{js,css}` and `static/list-reorder.js`, with native UIKit
  movement and presentation. Ported files keep their SPDX and author notices.

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

## catpawrunner — MIT

- Source: `hjdhnx/catpawrunner` `a7b29ea3ed9bcc05fa94105013d8f807f13152b8`.
- Package metadata author: 道长. The reviewed checkout declares MIT in `package.json`
  and does not contain a standalone license file.
- License: `catpawrunner-MIT.txt`.

Adapted portions:

- Cat startup and service compatibility were informed by `src/util/onlineRuntime.js`, but
  were rewritten for the QuickJS guest, finite Node-style capability bridge, installation
  private volume and per-instance loopback listener. The original unrestricted host
  `require`, `fs`, `process`, child process and VM execution path is not copied.
- Management protocol discovery and field mapping in `cat-management.js` adapt the route
  families represented by `src/plugins/api/onlineScriptAdapters.js`; requests go directly
  to the selected installation's current managed endpoint.
- Provider capture, Cat root-level extension and display metadata behavior in
  `pan-interceptor.js` and `cat-output.js` were informed by
  `runtimePanCapture.js`, `panmockDetailCodec.js` and `panDisplayMeta.js`.
  Provider login, list, transfer, direct-link and playback SDK implementations are not
  copied into the runner.

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

### Task-context derivation — revision 1

For task-context revision 1, the single-upstream-binary rule above is extended
by exactly one reviewed derived WASM exception:
`packages/script-host/src/quickjs/emscripten-module.wasm`, with its matching
`emscripten-module.mjs` loader in the same directory. This pair derives from the
same quickjs-emscripten `0.32.0` source at
`df4efb9ef2cb25c417ecb57986da462d11b244ed`, built with Emscripten `5.0.1`.
The upstream source archive SHA-256 is
`b6f342ad8e18bcff29ab4e78467d6e33c53c9e1a47986e58b91a1462beb31a8e`.

The local modifications in `scripts/quickjs/task-context.patch` propagate task
identity through Promise reactions and queued jobs. Reproduction is described
by `scripts/quickjs/build.py`; the patch, build script and generated pair are
pinned in `scripts/quickjs-runtime.json`. The existing upstream copyrights and
combined MIT license above also apply to the derived portions; the original
license text is unchanged and is redistributed as
`quickjs-emscripten-LICENSE.txt` and in the release-sync npm package's `LICENSE`.
The three npm packages and their registry identities remain unmodified.

## Node compatibility npm dependencies

The Cat finite Node-style bridge bundles the exact dependency closure recorded in
`package-lock.json`. Each packaged npm directory retains its own original license file.
The direct non-engine packages are `@opentelemetry/api` 1.9.0, `acorn` 8.15.0, `buffer` 6.0.3,
`events` 3.3.0, `path-browserify` 1.0.1, `readable-stream` 4.7.0,
`supports-color` 8.1.1 and `util` 0.12.5.

Acorn parses constant route declarations inside the QuickJS guest only when a
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
