# 可选资料接口

这是客户端接口约定，不包含可用服务地址或上游实现。客户端仅连接使用者配置的 HTTPS 服务。

| 方法与路径 | 功能 |
| --- | --- |
| `POST /v1/devices` | 取得匿名设备凭据 |
| `POST /v1/games/<appid>/resolve` | 查询已有游戏信息、版本和准备状态 |
| `POST /v1/games/<appid>/prepare` | 请求准备资料，返回 pending 或 ready |
| `GET /v1/games/<appid>/manifests/<depot>/<gid>` | 读取指定版本的清单 |
| `POST /v1/games/<appid>/complete` | 登记本次成功取得的资料 |
| `POST /v1/games/<appid>/upload` | 可选的所有者上传接口 |

`ready` 响应包含 `info`、`keys` 和 `manifests`：`keys` 将 depot ID 映射到对应密钥，`manifests` 将 depot ID 映射到 manifest GID。游戏信息与清单版本必须对应。包中可以有一个或多个 depot，不能假设固定文件数量。

普通请求使用设备 Bearer 凭据；所有者上传权限与普通读取权限分开。额度、缓存策略、更新频率和上游取资料的实现由服务部署者决定，公开客户端不包含这些运营配置。

可选公开查询路径为 `/steam-public/search`、`/steam-public/details` 和 `/steam-public/hosts`。实现时应限定固定功能、仅返回公开资料，不转发用户 Cookie 或任意目标 URL。
