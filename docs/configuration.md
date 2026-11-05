# 配置

界面可以设置 Steam 目录、代理、封面缓存上限、本地 SNI 和更新偏好。下载窗口单独选择游戏内容“直连”或“跟随设置中的代理”，默认直连；此选项不改变查询代理。并发默认自动（8–32），也可手动指定。公开构建默认没有资料服务地址，也没有生产更新签名公钥。

以下环境变量仅供自行部署或开发时配置。程序不自动加载 `.env` 文件；如需使用，请在启动进程前设置。

| 变量 | 用途 |
| --- | --- |
| `BEAR_METADATA_DIR` | 使用者自行准备的本地资料目录 |
| `BEAR_DOWNLOAD_SERVER` | 使用者配置的 HTTPS 资料接口根地址；默认空 |
| `BEAR_PUBLIC_RELAY` | 可选的公开商店查询接口根地址；默认空 |
| `BEAR_STEAM_HOSTS_URL` | 可选的网络地址提示镜像 |

本地资料按约定命名：`<appid>.info.json`、`<appid>.lua`、`<appid>.key.vdf`、`<depot>_<gid>.manifest`。其中 Lua 和 key.vdf 是密钥声明的可选输入；当前版本仍会读取所选 Steam 配置中已有的 depot key。

游戏信息 JSON 使用 `common`、`config` 和 `depots` 等字段，包含安装目录与 public 分支版本。选择逻辑见 `DownloadBackend/download_catalog.py`。

程序自身的 `data`、资料目录与自部署接口的认证文件不属于源码；不要提交到 Git。`.gitignore` 已排除常见运行数据、密钥文件和构建产物。

## 可选资源

`NetAssets` 与 `DownloadAssets` 的构建资源由脚本准备。补丁资源不包含在仓库中；仅在你有权使用和再分发的前提下自行准备，并审查对应代码和资源的兼容性。

## 自行发行

更新框架默认关闭自动检查和下载。自行发行前，配置自己的公开 Release 仓库与 ECDSA P-256 签名公钥。签名私钥仅放在发行者私有环境，不能进入源码、构建包或客户端。
