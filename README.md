# NonsensicalPatch

制作基于对比项目文件夹的补丁文件和应用补丁文件到旧补丁文件夹

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
[dataLength(z)]     8 or 0
[data]              z or 0

File:
Head                38
Block*n             BlockSize*x
```

## 项目

### NonsensicalPatch.Core

核心代码

### NonsensicalPatcher

补丁管理桌面应用，包含构建补丁，应用补丁，读取补丁功能，用于开发人员构建和测试补丁

### NonsensicalPatchUpdater

控制台应用，通过运行参数获取更新信息并进行更新，可以依次应用多个补丁  
