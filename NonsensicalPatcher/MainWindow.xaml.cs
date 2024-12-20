using Microsoft.WindowsAPICodePack.Dialogs;
using NonsensicalPatch.Core;
using System.Collections.Specialized;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace NonsensicalPatcher;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private static string[] DirNameChecks = new string[] { "donotship" };

    private string OldRootPath { get { return _oldRootPath; } set { _oldRootPath = value; } }
    private string NewRootPath
    {
        get
        {
            return _newRootPath;
        }
        set
        {
            _newRootPath = value;
            if (Directory.Exists(_newRootPath))
            {
                Queue<DirectoryInfo> dis = new Queue<DirectoryInfo>();
                dis.Enqueue(new DirectoryInfo(_newRootPath));

                while (dis.Count > 0)
                {
                    var crt = dis.Dequeue();
                    bool flag = false;
                    foreach (var item in DirNameChecks)
                    {
                        if (crt.Name.ToLower().Contains(item))
                        {
                            AppendMessage($"检测到名称包含\"{item}\"的文件夹，请确认是否需要加入到新版本文件中，文件夹路径:{crt.FullName}");
                            break;
                        }
                    }
                    if (flag)
                    {
                        break;
                    }
                    foreach (var item in crt.GetDirectories())
                    {
                        dis.Enqueue(item);
                    }
                }
            }
        }
    }
    private string PatchPath { get { return _patchPath; } set { _patchPath = value; } }
    private string PatchTargetRootPath { get { return _patchTargetRootPath; } set { _patchTargetRootPath = value; } }
    private CompressType CompressType
    {
        get
        {
            return _compressType;
        }
        set
        {
            _compressType = value;
            switch (value)
            {
                case CompressType.Gzip:
                    AppendMessage("Gzip压缩率稍低，但压缩速度较快");
                    break;
                case CompressType.Bzip2:
                    AppendMessage("Bzip2压缩率更高，但压缩速度非常慢（解压速度正常）");
                    break;
                default:
                    break;
            }
        }
    }

    private string _oldRootPath;
    private string _newRootPath;
    private string _patchPath;
    private string _patchTargetRootPath;
    private CompressType _compressType;

    private NameValueCollection _setting = ConfigurationManager.AppSettings;

    public MainWindow()
    {
        InitializeComponent();
        Logger logger = new Logger();
        logger.LogAction += AppendMessage;

        AppendMessage("¯\\_(ツ)_Ⳇ¯");

        _oldRootPath = GetStringSetting("OldRootPath");
        OldRootPathBox.Text = OldRootPath;
        _newRootPath = GetStringSetting("NewRootPath"); ;
        NewRootPathBox.Text = NewRootPath;
        _patchPath = GetStringSetting("PatchPath"); ;
        ExportPathBox.Text = PatchPath;
        _patchTargetRootPath = GetStringSetting("PatchTargetRootPath"); ;
        PatchTargetRootPathTextBox.Text = PatchTargetRootPath;
        _compressType = (CompressType)GetIntSetting("CompressType");
        CompressTypeBox.SelectedIndex = (int)CompressType;
    }

    #region UI Event

    private void Tab_SelectionChanged(object sender, RoutedEventArgs e)
    {
        if (Tab1.IsSelected)
        {
            OldPathSelect.Visibility = Visibility.Visible;
            NewPathSelect.Visibility = Visibility.Visible;
            PatchPathSelect.Visibility = Visibility.Visible;
            ApplyPathSelect.Visibility = Visibility.Collapsed;

            CompressTypeBox.Visibility = Visibility.Visible;

            BuildButton.Visibility = Visibility.Visible;
            ApplyButton.Visibility = Visibility.Hidden;
            ReadButton.Visibility = Visibility.Hidden;
        }
        else if(Tab2.IsSelected)
        {
            OldPathSelect.Visibility = Visibility.Collapsed;
            NewPathSelect.Visibility = Visibility.Collapsed;
            PatchPathSelect.Visibility = Visibility.Visible;
            ApplyPathSelect.Visibility = Visibility.Visible;

            CompressTypeBox.Visibility = Visibility.Hidden;

            BuildButton.Visibility = Visibility.Hidden;
            ApplyButton.Visibility = Visibility.Visible;
            ReadButton.Visibility = Visibility.Hidden;
        }
        else if (Tab3.IsSelected)
        {
            OldPathSelect.Visibility = Visibility.Collapsed;
            NewPathSelect.Visibility = Visibility.Collapsed;
            PatchPathSelect.Visibility = Visibility.Visible;
            ApplyPathSelect.Visibility = Visibility.Collapsed;

            CompressTypeBox.Visibility = Visibility.Hidden;

            BuildButton.Visibility = Visibility.Hidden;
            ApplyButton.Visibility = Visibility.Hidden;
            ReadButton.Visibility = Visibility.Visible;
        }
    }

    private void OldRootSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = OldRootPath;
        dialog.IsFolderPicker = true;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            OldRootPathBox.Text = dialog.FileName;
            SaveConfig();
        }
    }
    private void NewRootSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = NewRootPath;
        dialog.IsFolderPicker = true;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            NewRootPathBox.Text = dialog.FileName;
            SaveConfig();
        }
    }
    private void ExportPathSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = PatchPath;
        dialog.IsFolderPicker = false;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            ExportPathBox.Text = dialog.FileName;
            SaveConfig();
        }
    }
    private void PatchTargetRootPathSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = NewRootPath;
        dialog.IsFolderPicker = true;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            PatchTargetRootPathTextBox.Text = dialog.FileName;
            SaveConfig();
        }
    }

    private void OldRootPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        OldRootPath = OldRootPathBox.Text;
    }

    private void NewRootPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        NewRootPath = NewRootPathBox.Text;
    }

    private void ExportPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        PatchPath = ExportPathBox.Text;
    }

    private void PatchTargetRootPath_TextChanged(object sender, RoutedEventArgs e)
    {
        PatchTargetRootPath = PatchTargetRootPathTextBox.Text;
    }

    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        CompressType = (CompressType)CompressTypeBox.SelectedIndex;
        SaveConfig();
    }

    private void Build_ButtonClick(object sender, RoutedEventArgs e)
    {
        Build();
        SaveConfig();
    }
    private void Patch_ButtonClick(object sender, RoutedEventArgs e)
    {
        Patch();
        SaveConfig();
    }

    private void PatchRead_ButtonClick(object sender, RoutedEventArgs e)
    {
        ReadPatchFile();
        SaveConfig();
    }

    private void MessageTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        MessageTextScrollViewer.ScrollToBottom();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        MessageTextBox.Document.Blocks.Clear();
    }

    #endregion

    private async void Build()
    {
        DisableControl();
        if (string.IsNullOrEmpty(PatchPath))
        {
            AppendMessage($"补丁导出路径为空");
            EnableControl();
            return;
        }
        if (File.Exists(PatchPath))
        {
            MessageBoxResult result = MessageBox.Show("补丁导出路径已经存在文件，是否替换？",
                                          "确定",
                                          MessageBoxButton.YesNo,
                                          MessageBoxImage.Question);
            if (result == MessageBoxResult.No)
            {
                EnableControl();
                return;
            }
        }

        AppendMessage("开始创建补丁");

        FileStream fs;
        try
        {
            fs = new FileStream(PatchPath, FileMode.Create);
        }
        catch (Exception)
        {
            AppendMessage($"补丁导出路径：{PatchPath} 不正确或正被占用");
            EnableControl();
            return;
        }
        NonsensicalPatchWriter differ = new NonsensicalPatchWriter(OldRootPath, NewRootPath, CompressType, fs);

        Stopwatch sw = new Stopwatch();
        sw.Start();
        var patchSize = await differ.RunAsync();
        fs.Flush();
        fs.Close();

        sw.Stop();

        if (!differ.HasError)
        {
            AppendMessage($"补丁生成完毕，总共花费{sw.Elapsed.TotalMilliseconds}毫秒,补丁大小为{patchSize}字节");
        }
        else
        {
            foreach (var item in differ.ErrorMessage)
            {
                AppendMessage(item);
            }
        }
        EnableControl();
    }

    private async void Patch()
    {
        DisableControl();
        AppendMessage("开始应用补丁");
        NonsensicalPatchReader u = new NonsensicalPatchReader(PatchPath, PatchTargetRootPath);

        Stopwatch sw = new Stopwatch();
        sw.Start();
        await u.RunAsync();
        sw.Stop();
        if (u.HasError)
        {
            foreach (var item in u.ErrorMessage)
            {
                AppendMessage(item);
            }
        }
        else
        {
            this.AppendMessage($"应用完成，总共花费{sw.Elapsed.TotalMilliseconds}毫秒");
        }
        EnableControl();
    }

    private async void ReadPatchFile()
    {
        DisableControl();
        AppendMessage("开始读取补丁信息");
        NonsensicalPatchReader u = new NonsensicalPatchReader(PatchPath);
        await u.ReadAsync();
        var v = u.PatchInfo;
        if (u.HasError)
        {
            foreach (var item in u.ErrorMessage)
            {
                AppendMessage(item);
            }
        }
        else
        {
            AppendMessage($"补丁信息");
            AppendMessage($"补丁版本：{v.Version}");
            AppendMessage($"压缩格式：{v.CompressType}");
            AppendMessage($"MD5校验码：{v.MD5Hash.ToHex()}");
            AppendMessage($"块数量：{v.BlockCount}");

            for (int i = 0; i < v.Blocks.Count; i++)
            {
                AppendMessage($"块{i + 1} | 类型:{v.Blocks[i].PatchType} | 路径:{v.Blocks[i].Path} | 大小:{v.Blocks[i].DataSize}");
            }
        }
        AppendMessage("补丁信息读取完毕");
        EnableControl();
    }

    private string GetStringSetting(string key)
    {
        var value = _setting[key];
        if (value == null)
        {
            return string.Empty;
        }
        else
        {
            return value;
        }
    }

    private int GetIntSetting(string key)
    {
        var value = _setting[key];
        if (value == null
        || (int.TryParse(_setting[key], out var v) == false))
        {
            return 0;
        }
        else
        {
            return v;
        }
    }

    private void SaveConfig()
    {
        Configuration cfa = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
        cfa.AppSettings.Settings["OldRootPath"].Value = OldRootPath;
        cfa.AppSettings.Settings["NewRootPath"].Value = NewRootPath;
        cfa.AppSettings.Settings["PatchPath"].Value = PatchPath;
        cfa.AppSettings.Settings["PatchTargetRootPath"].Value = PatchTargetRootPath;
        cfa.AppSettings.Settings["CompressType"].Value = ((int)CompressType).ToString();
        cfa.Save();
    }

    private void EnableControl()
    {
        TabControl.IsEnabled = true;

        SelectOldPathButton.IsEnabled = true;
        SelectNewPathButton.IsEnabled = true;
        SelectPatchPathButton.IsEnabled = true;
        SelectApplyPathButton.IsEnabled = true;

        OldRootPathBox.IsEnabled = true;
        NewRootPathBox.IsEnabled = true;
        ExportPathBox.IsEnabled = true;
        PatchTargetRootPathTextBox.IsEnabled = true;

        CompressTypeBox.IsEnabled = true;
        BuildButton.IsEnabled = true;
        ApplyButton.IsEnabled = true;
        ReadButton.IsEnabled = true;
    }

    private void DisableControl()
    {
        TabControl.IsEnabled = false;

        SelectOldPathButton.IsEnabled = false;
        SelectNewPathButton.IsEnabled = false;
        SelectPatchPathButton.IsEnabled = false;
        SelectApplyPathButton.IsEnabled = false;

        OldRootPathBox.IsEnabled = false;
        NewRootPathBox.IsEnabled = false;
        ExportPathBox.IsEnabled = false;
        PatchTargetRootPathTextBox.IsEnabled = false;

        CompressTypeBox.IsEnabled = false;
        BuildButton.IsEnabled = false;
        ApplyButton.IsEnabled = false;
        ReadButton.IsEnabled = false;
    }

    private void AppendMessage(string newMessage)
    {
        MessageTextBox.Dispatcher.Invoke(DoAppendMessage, [newMessage]);
    }

    private void DoAppendMessage(string newMessage)
    {
        MessageTextBox.AppendText(newMessage + "\r");
    }
}