namespace NonsensicalPatchUpdater;

public class UpdateConfig
{
    /// <summary>
    /// 所有需要安装的补丁的配置信息
    /// </summary>
    public List<string> PatchUrls { get; set; }
    /// <summary>
    /// 补丁目标根目录
    /// </summary>
    public string TargetRootPath { get; set; }
    /// <summary>
    /// 补丁打完后自动执行的可执行文件路径,为空时不自动执行
    /// </summary>
    public string AutoStartPath { get; set; }
}