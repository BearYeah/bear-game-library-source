# Third-party notices

Bear 的原创代码按 GPL-3.0-only 发布。依赖的许可证不因整合而改变。自行分发构建产物时，应同时保留适用的许可证与版权说明。

| Component | License / reference | Usage |
| --- | --- | --- |
| .NET / WPF | MIT; see dotnet repositories and runtime notices | Desktop and runtime |
| CPython | Python Software Foundation License | Worker runtime |
| Requests | Apache-2.0 | HTTP client |
| urllib3 | MIT | Connection pooling and TLS integration |
| cryptography | Apache-2.0 OR BSD-3-Clause | Cryptographic primitives |
| python-zstandard / Zstandard | See upstream BSD license notices | Compression |
| PyInstaller | GPL-2.0-or-later with bootloader exception; see upstream | Worker packaging |
| curl | curl license; see upstream COPYING | Optional network runtime |

Watt Toolkit 与 SteamHostSync 是网络设计或地址提示的参考项目，不是 Bear 的内置资料服务。公开仓库没有整包引入 Watt Toolkit，也没有包含其运营配置。请勿将设计参考理解为游戏资料来源或项目背书。

Steam、游戏名称与第三方标识属于相应权利人。游戏文件、清单、密钥、资料包和补丁二进制不包含在本仓库的 GPL 授权内容中。本仓库的应用图标与像素页面插画随项目分发。

局域网联机使用可选的 Goldberg Steam Emulator 核心（LGPL-3.0）。固定版本核心在构建时获取并内置，运行时释放为可替换文件，也可由使用者导入其他版本；当前对应源码为 https://github.com/Detanup01/gbe_fork/tree/release-2026_09_27 。许可证正文见 Assets/LGPL-3.0.txt，GNU GPL 正文见 LICENSE。

SmartGoldbergEmu（LGPL-3.0-or-later）仅作为配置和启动交互的设计参考，本次没有复制其启动器代码。构建阶段按需获取的 7zr 来自 7-Zip 官方公共领域 SDK；源码见 https://www.7-zip.org/sdk.html 。Git 源码不附带这些预编译核心或工具；发行 EXE 会包含固定版本核心，其对应源码和许可证随发行提供。

SteamKit2 3.5.0（LGPL-2.1-only）用于匿名 Steam 会话和内容节点访问授权。对应源码：https://github.com/SteamRE/SteamKit/tree/3.5.0 ，许可证见 Assets/SteamKit-LGPL-2.1.txt。依赖 protobuf-net（Apache-2.0）、ZstdSharp.Port（MIT）及 System.IO.Hashing（MIT）。公开源码保留完整授权会话和下载调度实现，可替换依赖后重新构建。
