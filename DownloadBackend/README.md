# 下载模块

此目录公开下载工作进程、清单与分块解析、网络连接、安装登记和隔离测试。

`steam_manifest.py` 只处理使用者提供的资料及内容分发协议；没有资料收集渠道和运营上游适配器。`bear_cache_client.py` 的默认服务地址为空，可按公开协议自行配置。

`steam_network.py` 在连接失败时尝试本地 SNI 和地址提示；认证使用原始域名。`bear_downloader.py` 负责平台与语言选择、进度、续传以及安装登记。

运行测试：`python -B -m unittest network_tests`，或在项目根目录执行 `python Build/test.py`。
