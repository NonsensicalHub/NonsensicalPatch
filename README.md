# NonsensicalPatch

对比两个版本的文件夹，生成补丁，再把补丁应用到旧版本目录上。需要 [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0)。

``` text
dotnet build NonsensicalPatch.sln
```

| 程序 | 输出 |
| --- | --- |
| 制作、测试补丁 | `NonsensicalPatcher\bin\Debug\net8.0-windows\NonsensicalPatcher.exe` |
| 控制台更新 | `NonsensicalPatchUpdater\bin\Debug\net8.0\NonsensicalPatchUpdater.exe` |
| 窗口更新 | `NonsensicalPatchUpdater.GUI\bin\Debug\net8.0-windows7.0\NonsensicalPatchUpdater.GUI.exe` |

把 `Debug` 换成 `Release` 即是发布构建。Release 下更新程序完成后直接退出；Debug 下会留下日志，控制台版还会等待按键。

## 如何使用

### 制作补丁

打开 `NonsensicalPatcher`，留在「构建补丁」：

1. **旧版本文件根目录**：用户机器上现在的那一版。
2. **新版本文件根目录**：要发布的那一版。
3. **补丁路径**：要写出的补丁文件。该路径已有文件时会询问是否覆盖。
4. 选择压缩方式，点 **构建补丁**。

两个目录按相对路径对比：

| 情况 | 补丁块 |
| --- | --- |
| 只在新版本里 | `CreateFolder` / `CreateFile` |
| 只在旧版本里 | `RemoveFolder` / `RemoveFile` |
| 路径相同、内容变了 | `ModifyFile` |
| 路径变了、内容相同 | `MoveFile`；整棵子目录都相同则是 `MoveFolder` |
| 路径变了、内容也变了 | 删除旧文件，再写入新文件 |

`Gzip` 压缩更快，体积稍大。`Bzip2` 体积更小，制作时会慢很多，安装时速度正常。新版本目录里如果有名字包含 `donotship` 的文件夹，日志会提示确认，这些文件夹仍会被打进补丁。上次填写的路径和压缩方式会记在程序目录的 `NonsensicalPatcher.dll.config` 里。

带 `MoveFile` 或 `MoveFolder` 的补丁要用当前这版程序安装。更早的程序没有这两种块。

### 检查补丁

切到「读取补丁」，选择补丁文件，点 **读取补丁**。日志里会列出版本、压缩方式、MD5、块数量，以及每一块的类型、路径和数据大小。移动块还会写来源路径。

### 在本机试装

切到「应用补丁」：

1. **补丁路径**：上一步生成的文件。
2. **应用补丁文件根目录**：要被更新的旧版本目录。这个目录会被直接改写，试装前先留一份副本。
3. 点 **应用补丁**。

多个版本要逐个试时，按版本顺序各装一次。后一个补丁的「旧版本」应是前一个补丁装完后的目录。

### 发给用户安装

用户侧用 `NonsensicalPatchUpdater.exe` 或 `NonsensicalPatchUpdater.GUI.exe`。两者参数相同：

``` text
更新程序.exe <主程序进程ID> <UpdateConfig 的 JSON>
```

JSON 字段如下。名字大小写要一致。

| 字段 | 含义 |
| --- | --- |
| `PatchUrls` | 按顺序安装的补丁。可以是本地路径，也可以是 `http` / `https` 地址。本地文件不存在时会按网址下载 |
| `TargetRootPath` | 要更新的目录，必须已经存在 |
| `AutoStartPath` | 全部装完后要启动的程序。留空，或指向的文件不存在，则不启动 |

第一个参数是正在运行的主程序进程 ID。更新程序会等它退出，最多约 10 秒；超时则停止并报告主程序没有正常关闭。这个 ID 对应的进程已经不在时，会立刻开始安装。

PowerShell 示例：

``` powershell
$config = @{
    PatchUrls = @(
        "C:\patches\1.0-1.1.np",
        "https://example.com/patches/1.1-1.2.np"
    )
    TargetRootPath = "C:\Game"
    AutoStartPath = "C:\Game\Game.exe"
} | ConvertTo-Json -Compress

$gamePid = 1234
Start-Process .\NonsensicalPatchUpdater.exe -ArgumentList $gamePid, $config
```

`$gamePid` 换成主程序的进程 ID。窗口版把可执行文件换成 `NonsensicalPatchUpdater.GUI.exe` 即可。补丁按数组顺序安装，中间任何一步失败都会停下来，不再装后面的，也不会启动 `AutoStartPath`。

## 补丁文件结构

``` text
Value               Length

Head:
magic               16
patchVersion        1
compressType        1
md5                 16
blockCount(x)       4

Block:
blockType           1
pathLength(y)       2
pathUTF8Bytes       y
[dataLength(z)]     8 or 0   （仅 CreateFile / ModifyFile）
[data]              z or 0

MoveFile(5) / MoveFolder(6) 没有 data，主路径是新路径，后面再写旧路径：
oldPathLength(m)    2
oldPathUTF8Bytes    m

File:
Head                38
Block*n             BlockSize*x
```

`blockType`：`RemoveFile = 0`，`CreateFile = 1`，`ModifyFile = 2`，`RemoveFolder = 3`，`CreateFolder = 4`，`MoveFile = 5`，`MoveFolder = 6`。`compressType`：`Gzip = 0`，`Bzip2 = 1`。

## 项目

### NonsensicalPatch.Core

补丁的生成、读取和应用。`NonsensicalPatchWriter` 对比两个目录并写入流，`NonsensicalPatchReader` 读取补丁信息或把补丁装到目标目录。

### NonsensicalPatcher

桌面程序，用来构建、试装和查看补丁。

### NonsensicalPatchUpdater

控制台更新程序，按命令行参数依次安装多个补丁。

### NonsensicalPatchUpdater.GUI

窗口版更新程序，参数和控制台版相同，另外显示当前补丁、任务和进度。
