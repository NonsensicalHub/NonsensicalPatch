using NonsensicalPatch.Core;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;

namespace NonsensicalPatchWindowUpdater
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();
            StartPatch();
        }

        private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            MessageTextScrollViewer.ScrollToBottom();
        }

        private void ClearButton_Click(object sender, RoutedEventArgs e)
        {
            MessageTextBox.Document.Blocks.Clear();
        }

        private void AppendMessage(string newMessage)
        {
            MessageTextBox.Dispatcher.Invoke(DoAppendMessage, [newMessage]);
        }

        private void DoAppendMessage(string newMessage)
        {
            MessageTextBox.AppendText(newMessage + "\r");
        }


        private async void StartPatch()
        {
            bool debug = false;
#if DEBUG
            debug = true;
#endif
            if (debug)
            {
                this.Height = 450;
            }

            txt_CurrentPatch.Content = "当前补丁(加载中)";
            string[] args = Environment.GetCommandLineArgs();

            for (int i = 0; i < args.Length; i++)
            {
                AppendMessage($"参数{i}：{args[i]}");
            }

            AppendMessage("开始应用补丁");
            // 案例：控制台运行 start ".\Nonsensical Updater.exe" '"arg1" "arg2"'
            // 此时args第一个值是"arg1"，第二个值是"arg2"

            //第一个参数为主程序的id，第二个参数应为UpdateConfig的json序列化字符串
            if (args.Length < 3)
            {
                AppendMessage("缺少参数!");
                if (!debug) Environment.Exit(0);
                return;
            }

            AppendMessage($"等待主程序关闭");
            if (int.TryParse(args[1], out var mainProcessID))
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
                        AppendMessage($"主程序已关闭");
                        break;
                    }
                    Thread.Sleep(100);

                    count++;

                    AppendMessage($"等待中:{count}");
                    if (count > 100)
                    {
                        AppendMessage("程序未正常关闭!");
                        if (!debug) Environment.Exit(0);
                        return;
                    }
                }
            }
            else
            {
                AppendMessage("主程序id错误!");
                if (!debug) Environment.Exit(0);
                return;
            }

            var updateConfig = JsonSerializer.Deserialize<UpdateConfig>(args[2], JsonOptionsCreater.CreateDefaultOptions());

            if (updateConfig == null)
            {
                AppendMessage("参数错误!");
                if (!debug) Environment.Exit(0);
                return;
            }

            Logger log = new Logger();
            log.LogAction += AppendMessage;

            int patchCount = 1;

            foreach (var item in updateConfig.PatchUrls)
            {
                txt_CurrentPatch.Content = $"当前补丁({patchCount++}/{updateConfig.PatchUrls.Count})";
                txt_CurrentPatchUrl.Content = item;

                NonsensicalPatchReader updater = new NonsensicalPatchReader(item, updateConfig.TargetRootPath);
                updater.MissionStateChanged += OnMissionChanged;
                await updater.RunAsync();

                if (updater.HasError)
                {
                    AppendMessage("发生错误！");
                    foreach (var msg in updater.ErrorMessage)
                    {
                        AppendMessage(msg);
                    }
                    if (!debug) Environment.Exit(0);
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
                    AppendMessage($"无法执行：{updateConfig.AutoStartPath}");
                    if (!debug) Environment.Exit(0);
                    return;
                }
            }

            if (!debug) Environment.Exit(0);

        }

        private void OnMissionChanged(MissionState state)
        {
            txt_CurrentPatchMission.Content = state.MissionName;
            pro_LoadCurrentPatch.IsIndeterminate = state.IsIndeterminate;
            if (state.MaxSize != 0)
            {
                double currentSize = (double)state.CurrentSize / state.MaxSize * 100;
                pro_LoadCurrentPatch.Value = currentSize;
                txt_LoadCurrentPatch.Content = currentSize.ToString("f2") + "%";
            }
            else
            {
                txt_LoadCurrentPatch.Content = string.Empty;
            }
        }
    }
}