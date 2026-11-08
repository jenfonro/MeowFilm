# Windows 原生音质与空间策略

## 职责边界

自动与手动入口的共同边界见[歌曲资料与智能播放](../common/music/smart-playback.md#自动与手动选择)。

- 音质策略由原生 `MusicLibraryOwner` 唯一持有，设置页、官方播放链和洛雪音源播放链只消费该 owner。
- 设置页修改后立即保存并立即生效，不维护页面草稿、保存按钮、放弃按钮或重新读取按钮。
- 音质策略不代表解码器、声卡或输出设备已经验证对应格式；结果中的验证字段必须保持真实。
- 当前产品没有下载管理入口或 `music.online.download` 协议。缓存、播放许可和用户下载保持不同职责。

## 当前持久格式

- 文档命名空间：`music.library.v1`。
- 区域：`native-quality-policy`。
- 当前且唯一格式：

```json
{
  "schema": 1,
  "revision": 0,
  "request": "flac",
  "qualityOrder": ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"],
  "qualityEnabled": ["master", "spatial", "enhanced", "hires", "lossless", "high", "standard"],
  "spatialOrder": ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"],
  "spatialEnabled": ["dolby", "dts_x", "sony360", "sky", "vivid", "qq_atmos", "viper_atmos"]
}
```

- 全新文档尚未写入该区域时，owner 返回上述当前默认值，不产生写入。
- 已存在的区域必须完整符合当前格式；非法、缺字段、重复项、未知项或全部关闭时明确失败，不修复、不重写。
- 当前格式只接受 `auto`、`128k`、`192k`、`320k`、`flac`、`flac24bit`、`master` 请求。
- 至少保留一个有效音质类别；启用空间类别时至少保留一个空间子类。

## 原生协议

### 读取

`music.quality.settings.get {}`

返回：

```json
{
  "ok": true,
  "settings": {},
  "signature": "64位小写十六进制",
  "onlineSettings": {}
}
```

### 保存

`music.quality.settings.save`

参数：

```json
{
  "ifRevision": 0,
  "request": "auto",
  "qualityOrder": [],
  "qualityEnabled": [],
  "spatialOrder": [],
  "spatialEnabled": []
}
```

- 保存使用唯一 Library 文档的 CAS。
- 音质策略与脚本目标、歌词开关分别由各自设置协议写入；音质保存不得夹带或改写其他设置。
- 写入回执不确定时，调用方只读取一次当前权威状态判断请求是否已经落盘，不重放原 CAS。
- 当前状态与请求一致时接受权威结果；不一致时恢复权威值并报告保存失败。

## 播放选择

自动播放从首选档起沿 qualityOrder 向后跳禁用项，每档穷尽官方与脚本、缓存优先，再降级；auto 从首档开始。
同一请求对应的缓存若经解码证实为排序更高的已启用音质，自动播放可以直接复用并展示实际音质；低档缓存不能提前跳过高档，手动换档仍须命中指定档位。
已有录音缺少 types 且未声明 qualityKnown:true，可向声明该音质的脚本明确取链；明确反证则不派发。
`music.online.resolve` 本身只取一个具体音质，不执行自动回退。播放器手动换档不改设置、不降级，准备成功后才交接。
来源页使用独立手动策略投影，包含启用脚本全部兼容音质；明确选择及其精确续链不再套用自动禁用档位。自动播放仍读取持久策略。策略变更取消在途旧解析、拒绝旧缓存签名，但不重启已打开媒体。完整行为见[统一选源](music-official-selection.md)。
