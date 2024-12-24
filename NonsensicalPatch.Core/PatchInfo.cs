namespace NonsensicalPatch.Core;

public class PatchInfo
{
    public int Version;
    public CompressType CompressType;
    public byte[]? MD5Hash;
    public int BlockCount;
    public List<PatchBlock> Blocks = new List<PatchBlock>();
}

public class PatchBlock
{
    public BlockType PatchType;
    public string Path;
    public long DataSize;

    public PatchBlock(BlockType patchType, string path)
    {
        PatchType = patchType;
        Path = path;
    }
}
