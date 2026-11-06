# 配置

界面可以设置 Steam 目录、代理、封面缓存上限、本地 SNI 和更新偏好。下载窗口单独选择游戏内容“直连”或“跟随设置中的代理”，默认直连；此选项不改变查询代理。并发默认自动（8–32），也可手动指定。公开构建默认没有资料服务地址，默认关闭启动更新检查及客户端访问控制。

以下环境变量仅供自行部署或开发时配置。程序不自动加载 `.env` 文件；如需使用，请在启动进程前设置。

| 变量 | 用途 |
| --- | --- |
| `BEAR_METADATA_DIR` | 使用者自行准备的本地资料目录 |
| `BEAR_DOWNLOAD_SERVER` | 使用者配置的 HTTPS 资料接口根地址；默认空 |
| `BEAR_CLIENT_ACCESS_CONTROL` | 设为 `1` 后启用自己的客户端策略服务；默认不启用 |
| `BEAR_PUBLIC_RELAY` | 可选的公开商店查询接口根地址；默认空 |
| `BEAR_STEAM_HOSTS_URL` | 可选的网络地址提示镜像 |

本地资料按约定命名：`<appid>.info.json`、`<appid>.lua`、`<appid>.key.vdf`、`<depot>_<gid>.manifest`。其中 Lua 和 key.vdf 是密钥声明的可选输入；当前版本仍会读取所选 Steam 配置中已有的 depot key。

游戏信息 JSON 使用 `common`、`config` 和 `depots` 等字段，包含安装目录与 public 分支版本。选择逻辑见 `DownloadBackend/download_catalog.py`。

程序自身的 `data`、资料目录与自部署接口的认证文件不属于源码；不要提交到 Git。`.gitignore` 已排除常见运行数据、密钥文件和构建产物。

## 可选资源

`NetAssets` 与 `DownloadAssets` 的构建资源由脚本准备。补丁资源不包含在仓库中；仅在你有权使用和再分发的前提下自行准备，并审查对应代码和资源的兼容性。

## 更新网页与版本信息

更新检查读取 `BEAR_DOWNLOAD_SERVER` 所配置服务的 `GET /v1/client/update`，不下载 EXE。响应可以为 `null`，或：

```json
{"version":"1.8.20","url":"https://wwbhp.lanzoul.com/b004jv6lfi","notes":"此版本的更新内容"}
```

只在返回版本高于当前版本时提示。启动检查默认关闭，可以在设置里开启，也可以手动检查。用户确认后才打开蓝奏云网页，拒绝后不跳转。当前桌面跳转目标固定为 `Updates.cs` 中的 `UpdatePromptFlow.DownloadPage`，自行发行时应改为自己的更新网页；不会直接执行返回地址或自动安装程序。

`UpdateSignature.cs` 等历史发布准备代码不参与当前客户端的更新检查与关闭流程。公开构建不包含生产公钥或任何签名私钥。

## 可选客户端访问策略

公开构建无需连接维护者服务即可启动。自行运营时，设置 `BEAR_CLIENT_ACCESS_CONTROL=1`，并将 `BEAR_DOWNLOAD_SERVER` 指向自己的兼容 HTTPS 服务。客户端工作进程沿用软件代理和证书认证。

服务应实现设备登记 `POST /v1/devices`，返回本服务使用的设备令牌；随后通过令牌认证 `POST /v1/client/access`：

```json
{"hwid":"64 位 SHA-256 摘要","client_version":"1.8.20"}
```

返回示例：

```json
{
  "allowed": true,
  "code": "allowed",
  "message": "",
  "mode": "blacklist",
  "revision": 1,
  "checked_at": 1800000000,
  "offline_until": 1800086400,
  "update": null
}
```

`checked_at` 与 `offline_until` 为 Unix 秒。`update` 可包含上述更新信息。服务自行决定模式、白名单、黑名单及离线许可期限；当前建议许可期限为 24 小时。客户端在线期间约每 60 秒重新检查，收到拒绝时先保存下载进度，再显示访问状态和重试入口。

本地在当前 Windows 用户的 `Software\BearLibrary\ClientAccess` 注册表键保存与硬件摘要绑定的最近状态。拒绝状态包含 `CachedHandlerID` 标记，下一次启动仍先联网查询，服务重新允许后清除标记。断网时，最近一次拒绝继续生效；最近一次许可只在离线期限内有效。首次使用和许可到期需要联网。正常启动在后台检查，获准后直接进入主界面。界面不显示硬件摘要，原始硬件特征不随该请求发送。设置角落的设备编号来自匿名设备凭据的 SHA-256 前 12 位，与硬件摘要独立，并对应额度管理中的设备标签；不会显示凭据本身。

该机制可以管理正常使用本构建的设备，不能保证无法绕过。离线设备不会立即收到新策略；可修改的开源客户端也可以停用策略检查。机器更换主要硬件或固件身份变化可能产生新摘要。不要把本地标记称为永久封禁，也不要用无提示闪退代替可恢复的访问状态。
