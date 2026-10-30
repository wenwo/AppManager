# AppManager

Windows 桌面应用管理器：应用列表、资源分类与集合、图片查看、内置视频播放、桌面收纳与备份。

## 目录分离

- 开发目录：`D:\OWN\AppManager`，存放源码、Git 仓库、构建脚本和开发文档。
- 运行目录：`%LOCALAPPDATA%\AppManager`，存放 `AppManager.exe`、`libmpv-2.dll`、数据库、缓存、备份和日志。

`build.ps1` 默认将新构建发布到运行目录。应用运行中会拒绝覆盖，构建前请先从托盘菜单退出。

## 构建

要求：Windows 10/11 64 位（libmpv 仅提供 x64），.NET Framework 4.8（`csc.exe`），PowerShell 5.1。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` 会编译 `src` 下全部源码为单文件 `AppManager.exe` 并发布到 `%LOCALAPPDATA%\AppManager\AppManager.exe`，随后运行内置自测（`--selftest`，当前 62 项），日志写入运行目录的 `selftest.log`。

分类、集合、内容关系、配置和操作记录存入本机 SQLite 数据库，首次启动自动迁移 XML 并保留原件。列表支持索引分页，设置页提供操作记录、撤销、完整备份和恢复。表结构、索引、升级和恢复说明见 [DATABASE.md](DATABASE.md)。构建还会运行隔离的数据库回归；`-ScaleDatabaseTests` 可额外验证 30 万条数据。

## 视频播放器依赖（libmpv-2.dll）

内置播放器通过 P/Invoke 调用 libmpv。`libmpv-2.dll` 是约 112MiB 的二进制，直接入库会让仓库体积膨胀并触及托管平台限制，因此不入库，由构建流程按需下载。

首次构建时如果缺少 `libmpv-2.dll`，`build.ps1` 会自动执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\fetch-libmpv.ps1
```

脚本行为：

1. 从 nuget.org 下载固定版本的 `Endpne.LibMPV.Windows 0.41.0` 包（约 78MiB）。
2. 校验整个包的 SHA256：`99811FA17D8D14C64C5F35A230D8F77FAAB63AAA85097F67D272F781E677C252`。
3. 解出包内 `build/x64/libmpv-2.dll`，校验 SHA256：`38ACD030006062830D792A958C5F2ADC293FA965996797980EC73D9610C309F4`。
4. 把 DLL 放到运行目录的 `AppManager.exe` 同目录。

常用参数：

```powershell
# 已存在时跳过；-Force 强制重新下载覆盖
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\fetch-libmpv.ps1 -Force

# 输出到指定位置
powershell -NoProfile -ExecutionPolicy Bypass -File .\tools\fetch-libmpv.ps1 -Destination D:\path\libmpv-2.dll
```

离线或无法访问 nuget.org 时：

- 设置 `APPMANAGER_SKIP_LIBMPV_FETCH=1` 后再构建，构建会继续，但播放器显示占位提示。
- 手动从 <https://www.nuget.org/packages/Endpne.LibMPV.Windows/0.41.0> 下载 `.nupkg`，改名为 `.zip`，解出 `build/x64/libmpv-2.dll` 放到 `AppManager.exe` 同目录。
- libmpv 使用 LGPL 协议，许可文本见仓库根目录 `libmpv-LICENSE.txt`。

## 其他

- `AppManager.exe`、`selftest.log`、`config.xml`、收纳/快捷中心数据等运行时产物位于 `%LOCALAPPDATA%\AppManager`，不进入开发仓库。
- 本机私有规则文件（`AGENTS.md`、`CLAUDE.md`、`.trae/rules/project_rules.md`）只在本机生效，不提交。
