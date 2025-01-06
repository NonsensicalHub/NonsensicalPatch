using NonsensicalPatch.Core;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace NonsensicalPatchUpdater
{
    internal class Updater
    {
        public bool IsEnd = false;
        private string[] _args;
        private bool _debug;


        private StringBuilder _log;

        public Updater(string[] args, bool debug)
        {
            _args = args;
            _debug = debug;
        }

        public async void Run()
        {
            string line = new string('-', Console.BufferWidth);

            if (_debug)
            {
                line = line.Remove(2, 10).Insert(2, "debug mode");
                _log = new StringBuilder();
            }

            Console.CursorVisible = false;
            SafeSetCursorPosition(0, 0);
            Console.WriteLine(line);
            Console.WriteLine("当前补丁链接:");
            Console.WriteLine("当前任务:");
            Console.WriteLine("当前任务进度:");
            Console.WriteLine(line);

            await DoRun();

            if (_debug)
            {
                SafeSetCursorPosition(0, 5);
                Console.WriteLine(_log.ToString());
            }
            IsEnd = true;
        }

        private async Task DoRun()
        {
            WriteLog("Nonsensical Updater Start!");

            for (int i = 0; i < _args.Length; i++)
            {
                WriteLog($"参数{i}：{_args[i]}");
            }

            // 案例：控制台运行 start ".\Nonsensical Updater.exe" '"arg1" "arg2"'
            // 此时args第一个值是"arg1"，第二个值是"arg2"

            //第一个参数为主程序的id，第二个参数应为UpdateConfig的json序列化字符串
            if (_args.Length < 2)
            {
                WriteLog("缺少参数!");
                return;
            }

            WriteLog($"等待主程序关闭");
            if (int.TryParse(_args[0], out var mainProcessID))
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
                        WriteLog($"主程序已关闭");
                        break;
                    }
                    Thread.Sleep(100);

                    count++;

                    WriteLog($"等待中:{count}");
                    if (count > 100)
                    {
                        WriteLog("程序未正常关闭!");
                        return;
                    }
                }
            }
            else
            {
                WriteLog("主程序id错误!");
                return;
            }

            var updateConfig = JsonSerializer.Deserialize<UpdateConfig>(_args[1], JsonOptionsCreater.CreateDefaultOptions());

            if (updateConfig == null)
            {
                WriteLog("参数错误!");
                return;
            }

            Logger log = new Logger();
            log.LogAction += WriteLog;

            var _patchCount = updateConfig.PatchUrls.Count;
            var _currentPatchIndex = 0;
            foreach (var item in updateConfig.PatchUrls)
            {
                _currentPatchIndex++;
                SafeSetCursorPosition(0, 1);
                Console.WriteLine(FillToWindowWidth($"当前补丁链接({_currentPatchIndex}/{_patchCount}):{item}"));

                NonsensicalPatchReader updater = new NonsensicalPatchReader(item, updateConfig.TargetRootPath);
                updater.MissionStateChanged += OnMissionStateChanged;
                await updater.RunAsync();

                if (updater.HasError)
                {
                    WriteLog("发生错误!错误日志如下：");
                    foreach (string msg in updater.ErrorMessage)
                    {
                        WriteLog(msg);
                    }
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
                    WriteLog($"无法执行：{updateConfig.AutoStartPath}");
                    return;
                }
            }
        }

        private void OnMissionStateChanged(MissionState state)
        {
            SafeSetCursorPosition(0, 2);
            Console.WriteLine(FillToWindowWidth($"当前任务:{state.MissionName}"));

            if (state.IsIndeterminate)
            {
                Console.WriteLine(FillToWindowWidth("当前任务进度:加载中"));
            }
            else
            {
                double value = (double)state.CurrentSize / state.MaxSize;
                double countd = value * 10 + 1;
                int count = Math.Clamp((int)countd, 0, 10);
                StringBuilder sb = new StringBuilder();
                sb.Append('[');
                sb.Append('█', count);
                sb.Append(' ', 10 - count);
                sb.Append(']');
                Console.WriteLine(FillToWindowWidth($"当前任务进度:{sb.ToString()}"));
            }
        }

        private void WriteLog(string msg)
        {
            if (!_debug) return;
            _log.AppendLine(msg);
        }

        private string FillToWindowWidth(string str)
        {
            int emptyLength = Console.BufferWidth - str.Length;
            if (emptyLength > 0)
            {
                str += new string(' ', emptyLength / 2);  //除以2避免2宽度的中文字体导致计算错误的问题（临时解决方案）
            }
            return str;
        }

        public void SafeSetCursorPosition(int left, int top)
        {
            if (left < Console.BufferWidth && top < Console.BufferHeight)
            {
                Console.SetCursorPosition(left, top);
            }
        }
    }
}
