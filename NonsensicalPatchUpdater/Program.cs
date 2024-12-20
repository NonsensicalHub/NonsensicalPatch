using NonsensicalPatch.Core;
using NonsensicalPatchUpdater;
using System.Diagnostics;
using System.Text.Json;

bool debug = false;
#if DEBUG
debug = true;
#endif

Console.WriteLine("Nonsensical Updater Start!");

for (int i = 0; i < args.Length; i++)
{
    Console.WriteLine($"参数{i}：{args[i]}");
}

// 案例：控制台运行 start ".\Nonsensical Updater.exe" '"arg1" "arg2"'
// 此时args第一个值是"arg1"，第二个值是"arg2"

//第一个参数为主程序的id，第二个参数应为UpdateConfig的json序列化字符串
if (args.Length < 2)
{
    Console.WriteLine("缺少参数!按任意键关闭");
    if (debug) Console.ReadKey();
    return;
}

Console.WriteLine($"等待主程序关闭");
if (int.TryParse(args[0], out var mainProcessID))
{
    int count = 0;
    while (true)
    {
        try
        {
            var p = Process.GetProcessById(mainProcessID);
        }
        catch (Exception)
        {
            Console.WriteLine($"主程序已关闭");
            break;
        }
        Thread.Sleep(100);

        count++;

        Console.WriteLine($"等待中:{count}");
        if (count > 100)
        {
            Console.WriteLine("程序未正常关闭!按任意键关闭");
            if (debug) Console.ReadKey();
            return;
        }
    }
}
else
{
    Console.WriteLine("主程序id错误!按任意键关闭");
    if (debug) Console.ReadKey();
    return;
}

var updateConfig = JsonSerializer.Deserialize<UpdateConfig>(args[1], JsonOptionsCreater.CreateDefaultOptions());

if (updateConfig == null)
{
    Console.WriteLine("参数错误!按任意键关闭");
    if (debug) Console.ReadKey();
    return;
}

Logger log = new Logger();
log.LogAction += Console.WriteLine;

foreach (var item in updateConfig.PatchUrls)
{
    NonsensicalPatchReader updater = new NonsensicalPatchReader(item, updateConfig.TargetRootPath);
    await updater.RunAsync();

    if (updater.HasError)
    {
        Console.WriteLine("发生错误！按任意键关闭");
        if (debug) Console.ReadKey();
        return;
    }
}

if (File.Exists(updateConfig.AutoStartPath))
{
    try
    {
        Process.Start(updateConfig.AutoStartPath);
    }
    catch (Exception)
    {
        Console.WriteLine($"无法执行：{updateConfig.AutoStartPath}");
        if (debug) Console.ReadKey();
        return;
    }
}

if (debug) Console.ReadKey();