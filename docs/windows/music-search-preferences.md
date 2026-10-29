# Windows 原生搜索设置与历史

## 职责边界

- 搜索设置和搜索历史由原生 `MusicLibraryOwner` 唯一持有，不经 Node、网页后台或第二个设置服务。
- 歌曲搜索与歌单搜索分别配置；每种搜索只有 `aggregate` 或 `single` 两种模式。
- 设置页修改后立即保存并立即生效，不维护页面草稿、保存按钮或重新读取按钮。
- 搜索历史只提供“清空搜索历史”操作；产品页面不逐条展示、重开或删除历史记录。

## 当前持久格式

两项数据都位于 `music.library.v1` 当前 scope 中。

### `native-search-policy`

```json
{
  "schema": 2,
  "revision": 0,
  "songs": {
    "mode": "aggregate",
    "platform": "wy",
    "order": ["wy", "tx", "kw", "kg", "mg"],
    "enabled": ["wy", "tx", "kw", "kg", "mg"]
  },
  "playlists": {
    "mode": "aggregate",
    "platform": "wy",
    "order": ["wy", "tx", "kw", "kg", "mg"],
    "enabled": ["wy", "tx", "kw", "kg", "mg"]
  }
}
```

- `order` 必须完整且只包含五个平台。
- `enabled` 可以为空，但必须按 `order` 中的相对顺序排列且不得重复。
- `platform` 必须是五个平台之一，在单平台模式下作为唯一请求目标。
- 全新文档尚未写入该区域时返回上述当前默认值，不产生写入。
- 已存在区域必须完整符合 schema 2；其他格式明确失败，不转换、不映射、不回退读取。

### `native-search-history`

```json
{
  "schema": 1,
  "revision": 0,
  "items": [
    { "text": "关键词", "kind": "songs", "platform": "aggregate" }
  ]
}
```

- 关键词修剪后去重，新记录置顶，最多 60 项。
- `kind` 只接受 `local`、`songs`、`playlists`。
- 本地记录的平台必须是 `local`；在线记录的平台只接受五个平台或 `aggregate`。
- 无效字符、控制字符、非法 Unicode、过长文本或未知字段明确失败。

## 原生协议

- `music.search.settings.get {}`：读取唯一当前设置。
- `music.search.settings.save { ifRevision, songs, playlists }`：以完整当前状态执行 CAS。
- `music.search.history.get {}`：读取历史及 revision。
- `music.search.history.record { text, kind, platform }`：由 owner 串行去重并置顶。
- `music.search.history.clear { ifRevision, confirm:true }`：清空全部历史。

写入回执不确定或 revision 冲突时，设置消费者只读取一次当前权威状态：

- 当前状态与本次完整请求一致时接受结果，不重放写入。
- 当前状态与请求不一致时显示保存失败或已被更新，并继续使用权威状态。
- 读取失败时关闭编辑准入；不得继续用未确认的内存状态派发搜索。

## 请求生命周期

- App 初始化只读取当前搜索设置，不预取搜索历史。
- 搜索历史仅在设置页或搜索发现页首次可见时读取；同一代次只有一个在途读取，
  离开最后一个消费者时取消并排空，重复进入复用已经确认的当前状态。
- 搜索提交返回完整历史后直接采用该当前结果，不再额外读取。
- 进入在线搜索时只启动当前可见的歌曲或歌单类型，不预取隐藏类型。
- 聚合模式只请求该类型当前启用的平台；单平台模式只请求选定平台。
- 设置变化先退休并排空当前聚合搜索代次，再保存并更新派发策略。
- 同一搜索身份和参数在同一代次内只有一个在途请求；筛选、页面和查询变化取消旧代次。
- 分页记录各平台游标和结束状态，到达末尾后不得重复请求同一页。
- 失败只影响对应平台和搜索类型，不触发全部页面或所有平台重新加载。
