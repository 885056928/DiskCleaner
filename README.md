# DiskCleaner · C 盘清理

DiskCleaner 是一款中文 Windows 桌面工具，可查看 C 盘的已用和可用空间、清理缓存与临时文件，并查找、定位或删除大文件。操作流程参考 [360 安全卫士的系统盘瘦身和查找大文件](https://weishi.360.cn/product.html)。

## 下载

从 [GitHub Releases](https://github.com/885056928/DiskCleaner/releases/latest) 下载 [DiskCleaner.exe](https://github.com/885056928/DiskCleaner/releases/latest/download/DiskCleaner.exe)。发布版适用于 Windows x64，已包含 .NET 10 桌面运行时，下载后可直接双击运行。系统版本要求见 [Microsoft 的 .NET 支持列表](https://learn.microsoft.com/zh-cn/dotnet/core/install/windows#supported-versions)。

## 使用方法

### 清理缓存与临时文件

1. 在「垃圾清理」页点击「扫描清理项」。
2. 单击类别查看文件明细，勾选要清理的类别。
3. 点击「清理所选」，核对文件数量和大小后确认。清理完成后，可用空间会自动刷新。

| 清理项 | C 盘上的扫描位置 |
| --- | --- |
| 用户临时文件 | `%LOCALAPPDATA%\Temp` |
| Windows 临时文件 | `%WINDIR%\Temp` |
| DirectX 着色器缓存 | `%LOCALAPPDATA%\D3DSCache` |
| 资源管理器缩略图缓存 | `%LOCALAPPDATA%\Microsoft\Windows\Explorer` 下的 `thumbcache_*.db` |

清理项只纳入创建和修改时间均已超过 24 小时的文件。

### 查找和删除大文件

1. 在「大文件」页选择 100 MB、500 MB 或 1 GB 作为最小大小，点击「扫描大文件」。列表默认按大小降序展示，最多显示最大的 500 个匹配项。
2. 单击选中一行。按住 **Ctrl** 可逐项多选，按住 **Shift** 可连续多选。单选时可点击「在资源管理器中定位」。
3. 点击「删除」并确认。删除成功的文件会从列表移除，跳过的文件会保留。

### 删除与扫描规则

- 确认后执行永久删除，文件无法从回收站恢复。大文件手动删除不受 24 小时限制。
- 程序按当前 Windows 账户的权限运行。删除前会再次核对文件身份，跳过被占用、受保护以及扫描后被替换或修改的文件。程序安装目录和公共应用数据目录中的大文件仅供查看或定位。
- 大文件扫描跳过 Windows 安装目录、回收站、系统卷信息、分页和休眠文件、重解析点及无法访问的位置。扫描可随时取消。
- 删除结果统计的是文件逻辑大小，磁盘实际可用空间以顶部容量为准。

## 构建和验证

需要 Windows 和 .NET 10 SDK。在 PowerShell 中执行以下命令。

```powershell
git clone https://github.com/885056928/DiskCleaner.git
Set-Location DiskCleaner
dotnet build -c Release
dotnet run --project tests/DiskCleaner.Checks -c Release --no-build
dotnet publish src/DiskCleaner/DiskCleaner.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=embedded -o artifacts/win-x64
```

发布文件位于 `artifacts/win-x64/DiskCleaner.exe`。验证程序在自动创建的隔离目录中测试扫描、删除和文件保护规则。符号链接测试需要当前账户具备创建符号链接的权限。
