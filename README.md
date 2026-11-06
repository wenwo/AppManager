# AppManager

Windows 桌面应用管理器：应用列表、资源分类与集合、图片查看、内置视频播放、桌面收纳与备份。

软件页面只收录软件（程序、快捷方式、打包应用），只能通过「+ 添加」登记程序，不接受外部拖入、粘贴或新建文件；文件和文件夹的导入、复制、剪切、粘贴只在资源页面进行。桌面收纳只收软件快捷方式，指向文件夹或文档的快捷方式留在桌面。

## 集合文件夹

资源分类下的集合支持多层嵌套。进入集合后在内容区空白处右键选择「新建子文件夹…」，或在集合行右键、侧栏底栏创建；这些操作只创建集合归属，不会在磁盘上创建文件夹，也不会搬动资源文件。

侧栏按层级缩进显示，箭头可展开/收起；点击集合行取得键盘焦点后，可用上下键移动、左右键展开收起或回到父级、回车进入、空格切换展开状态。信息页的成员数只统计当前文件夹的直接成员，并单独显示直接子文件夹数量；集合内搜索也只匹配当前文件夹的直接成员。首版不支持拖动集合改变父级。

数据库升级后不再支持旧版程序直接打开；含嵌套集合的 `.ambak` 备份也不支持降级导入，因为旧版会忽略父集合字段。需要回退时请使用升级前的完整备份。

## 搜索范围

软件/资源搜索只匹配当前模式。未进入集合时，搜索限定在左侧当前分类；在“全部软件/全部资源”中搜索则覆盖整个模式。进入集合后，搜索只匹配该集合的直接成员，不递归子集合。条目名称或所属分类/集合名称命中时都会返回结果，但不越出当前搜索范围。

## 图片格式

内置查看器支持 JPG/JPEG、PNG、GIF、BMP、WebP。RW2 通过系统 Windows Imaging Component 解码，需要安装 Microsoft Raw Image Extension；未安装时缩略图显示占位，打开文件会提示安装，不会回退到不支持 RAW 的 GDI+ 解码。RW2 的列表和属性页拍摄时间优先读取 EXIF `DateTimeOriginal`，再回退 `DateTimeDigitized`、`DateTime`；元数据读取不设文件大小上限，查看器仍保留像素总量保护。

## 视频格式

内置播放器支持 MP4、MKV、AVI、MOV、WMV、FLV、WebM、M4V 和 3GP（`.3gp`）。3GP 只是容器，能否播放取决于内部音视频编码；当前构建已验证 H.264/AAC、H.263/AMR-NB 和无音轨 3GP 样本，其他编码组合未做完整回归。

## 目录分离

- 开发目录：`D:\OWN Work\AppManager`，存放源码、Git 仓库、构建脚本和开发文档。
- 运行目录：`%LOCALAPPDATA%\AppManager`，存放 `AppManager.exe`、`libmpv-2.dll`、数据库、缓存、备份和日志。

`build.ps1` 默认将新构建发布到运行目录：先写入同目录暂存文件，自测和数据库回归通过后再原子替换。应用运行中会拒绝覆盖，构建前请先从托盘菜单退出。

## 资源存储位置

在“照片”分类的“+ 添加”菜单中可选择“整理本机照片…”。程序只读扫描所有本地固定磁盘，确认后将照片复制到当前已启用的资源存储位置，保留原文件，不新增或修改存储位置。已有资源存储位置会跳过；已完成的复制记录用于避免重复扫描时再次制造副本。照片按 EXIF 拍摄时间归入“照片”分类下的“2025年”等集合，无拍摄时间时回退到文件修改时间。扫描可取消，不可读目录和文件会在确认前汇总提示。为减少大容量整理时的卡顿，扫描处理最多约 100 张/秒，复制主数据和备用数据流合计限速约 32 MB/s，始终使用单个后台任务；普通手动导入不受此限速影响。

设置页的「存储位置」支持添加多个资源存储根目录，列表顺序就是导入时的优先顺序。同一卷内移动只用硬链接，不要求额外空间；复制或跨卷移动会先计算文件/文件夹大小，要求目标位置剩余空间至少再保留 1 GB。复制中途遇到磁盘满时，程序清理本批临时副本并改用下一个启用的位置，源文件保持不变；一个文件或文件夹不会拆分到多个位置。

停用某个位置后不再向它导入新文件，但仍会识别其中已有的库内文件；全部位置都停用时不接收新资源，也不会偷偷回退到默认目录。旧版单一路径会自动成为第一个启用位置，配置文件仍把第一个启用位置写入旧字段供旧版本读取。导入 `.ambak` 备份包时保留本机的位置列表。

## 本机音乐扫描

资源页「音乐」分类的「+ 添加」菜单提供「扫描本机音乐…」和「清理失效音乐记录…」。扫描本机固定磁盘和已连接可移动磁盘中的支持音频格式，跳过链接/联接点目录、回收站和 AppManager 数据目录，可随时取消；网络位置和光驱不扫描。确认后复制到资源存储位置的「音乐」目录并保留源文件。资源复制会登记原始来源路径，重复扫描、目标改名后再次扫描都复用已有条目；来源已在其他资源分类时只补音乐归属，用户主动排除的音乐保持排除。不可读位置在确认前汇总提示，云端按需文件会提示复制时可能下载。按扩展名识别，不解析音频内容，不同路径的相同歌曲仍可能分别收录。清理只移除原文件已不存在且父目录仍可访问的数据库记录，确认后二次复核；移动盘离线、网络断开时跳过，磁盘和回收站文件始终不动。

## 构建

要求：Windows 10/11 64 位（libmpv 仅提供 x64），.NET Framework 4.8（`csc.exe`），PowerShell 5.1。

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
```

`build.ps1` 会编译 `src` 下全部源码为单文件 `AppManager.exe` 并发布到 `%LOCALAPPDATA%\AppManager\AppManager.exe`，随后运行内置自测（`--selftest`，当前 66 项），日志写入运行目录的 `selftest.log`。

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
5. 同步复制 `libmpv-LICENSE.txt` 到 DLL 同目录。

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

- `AppManager.exe`、`libmpv-2.dll`、`libmpv-LICENSE.txt`、`selftest.log`、数据库、扫描缓存、旧配置留底 `config.xml.before-sqlite`、收纳/快捷中心数据和备份等运行时产物位于 `%LOCALAPPDATA%\AppManager`，不进入开发仓库。
- AI 助手规则统一维护在 `AGENTS.md`；`CLAUDE.md` 和 `.trae/rules/project_rules.md` 只做入口引用，随仓库同步，保证换机器后规则一致。
