namespace NonsensicalPatch.Core;

public static class BasicInfo
{
    public static byte[] magic= [0x4e, 0x6f , 0x6e, 0x73, 0x65, 0x6e, 0x73, 0x69, 0x63, 0x61, 0x6c, 0x50, 0x61, 0x74, 0x63, 0x68];  //NonsensicalPatch
    public const byte PatchVersion = 1;
}

public enum BlockType
{
    RemoveFile = 0, //移除文件
    CreateFile = 1, //创建文件
    ModifyFile = 2, //修改文件
    RemoveFolder = 3,  //移除文件夹
    CreateFolder = 4,  //创建文件夹
    MoveFile = 5, //移动文件。主路径是新路径，块末尾另有旧路径
    MoveFolder = 6, //移动文件夹。主路径是新路径，块末尾另有旧路径
}

public enum CompressType
{
    Gzip = 0,
    Bzip2 = 1,
}