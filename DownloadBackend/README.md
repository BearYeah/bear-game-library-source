# 下载模块

此目录公开下载工作进程、清单与分块解析、网络连接、安装登记和隔离测试。

`steam_manifest.py` 只处理使用者提供的资料及内容分发协议；没有资料收集渠道和运营上游适配器。`bear_cache_client.py` 的默认服务地址为空，可按公开协议自行配置。

`steam_network.py` 在连接失败时尝试本地 SNI 和地址提示；认证使用原始域名。`bear_downloader.py` 负责平台与语言选择、进度、续传以及安装登记。

1.8.18 支持本体加选中 DLC，或仅补装选中 DLC。任务按本体组织；补装时自动定位本体安装目录，也允许手动指定，并按清单的相对路径写入其中。仅补装保留现有本体安装记录；移除共享目录的 DLC 任务不能删除本体文件。

可下载 DLC 必须有独立内容文件、对应版本 manifest 和 depot key。没有独立文件的 DLC 可能随本体内容提供。界面中的 DLC 条目不等于每项都存在独立文件包。原理与核实范围见 [下载设计](../docs/download-design.md)。

运行测试：`python -B -m unittest network_tests`，或在项目根目录执行 `python Build/test.py`。
