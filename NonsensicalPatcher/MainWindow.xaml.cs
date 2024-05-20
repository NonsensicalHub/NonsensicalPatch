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

        _oldRootPath = GetSetting("OldRootPath");
        OldRootPathBox.Text = _oldRootPath;
        _newRootPath = GetSetting("NewRootPath"); ;
        NewRootPathBox.Text = _newRootPath;
        _patchPath = GetSetting("PatchPath"); ;
        ExportPathBox.Text = _patchPath;
        _patchTargetRootPath = GetSetting("PatchTargetRootPath"); ;
        PatchTargetRootPathTextBox.Text = _patchTargetRootPath;
        if (int.TryParse(ConfigurationManager.AppSettings["CompressType"], out var v))
        {
            _compressType = (CompressType)v;
            CompressTypeBox.SelectedIndex = (int)_compressType;
        }
        else
        {
            _compressType = CompressType.Gzip;
            CompressTypeBox.SelectedIndex = 0;
        }

        AppendMessage("¯\\_(ツ)_Ⳇ¯");
    }

    #region UI Event

    private void OldRootSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = _oldRootPath;
        dialog.IsFolderPicker = true;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            OldRootPathBox.Text = dialog.FileName;
            _oldRootPath = dialog.FileName;
            SaveConfig();
        }
    }
    private void NewRootSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = _newRootPath;
        dialog.IsFolderPicker = true;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            NewRootPathBox.Text = dialog.FileName;
            _newRootPath = dialog.FileName;
            SaveConfig();
        }
    }
    private void ExportPathSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = _patchPath;
        dialog.IsFolderPicker = false;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            ExportPathBox.Text = dialog.FileName;
            _patchPath = dialog.FileName;
            SaveConfig();
        }
    }
    private void PatchTargetRootPathSelect_ButtonClick(object sender, RoutedEventArgs e)
    {
        CommonOpenFileDialog dialog = new CommonOpenFileDialog();
        dialog.InitialDirectory = _newRootPath;
        dialog.IsFolderPicker = true;
        if (dialog.ShowDialog() == CommonFileDialogResult.Ok)
        {
            PatchTargetRootPathTextBox.Text = dialog.FileName;
            _patchTargetRootPath = dialog.FileName;
            SaveConfig();
        }
    }

    private void OldRootPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        _oldRootPath = OldRootPathBox.Text;
    }
    private void NewRootPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        _newRootPath = NewRootPathBox.Text;
    }
    private void ExportPath_TextChanged(object sender, TextChangedEventArgs e)
    {
        _patchPath = ExportPathBox.Text;
    }
    private void PatchTargetRootPath_TextChanged(object sender, RoutedEventArgs e)
    {
        _patchTargetRootPath = PatchTargetRootPathTextBox.Text;
    }

    private void ComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _compressType = (CompressType)CompressTypeBox.SelectedIndex;
        SaveConfig();
    }

    private void Build_ButtonClick(object sender, RoutedEventArgs e)
    {
        Build();
    }
    private void Patch_ButtonClick(object sender, RoutedEventArgs e)
    {
        Patch();
    }

    private void PatchRead_ButtonClick(object sender, RoutedEventArgs e)
    {
        ReadPatchFile();
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
        if (string.IsNullOrEmpty(_patchPath))
        {
            AppendMessage($"补丁导出路径为空");
            return;
        }

        AppendMessage("开始创建补丁");

        FileStream fs;
        try
        {
            fs = new FileStream(_patchPath, FileMode.Create);
        }
        catch (Exception)
        {
            AppendMessage($"补丁导出路径：{_patchPath} 不正确或正被占用");
            return;
        }
        NonsensicalPatchWriter differ = new NonsensicalPatchWriter(_oldRootPath, _newRootPath, _compressType, fs);

        Stopwatch sw = new Stopwatch();
        sw.Start();
        await differ.RunAsync();
        fs.Flush();
        fs.Close();

        sw.Stop();

        if (!differ.HasError)
        {
            AppendMessage($"补丁生成完毕，总共花费{sw.Elapsed.TotalMilliseconds}ms,");
        }
        else
        {
            foreach (var item in differ.ErrorMessage)
            {
                AppendMessage(item);
            }
        }
    }

    private async void Patch()
    {
        AppendMessage("开始测试");
        NonsensicalPatchReader u = new NonsensicalPatchReader(_patchPath, _patchTargetRootPath);

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
            this.AppendMessage($"测试完成，总共花费{sw.Elapsed.TotalMilliseconds}ms");
        }
    }

    private async void ReadPatchFile()
    {
        AppendMessage("开始加载补丁信息");
        NonsensicalPatchReader u = new NonsensicalPatchReader(_patchPath);
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
            AppendMessage($"MD5校验码：{v.MD5Hash}");
            AppendMessage($"块数量：{v.BlockCount}");

            for (int i = 0; i < v.Blocks.Count; i++)
            {
                AppendMessage($"块{i + 1} | 类型:{v.Blocks[i].PatchType} | 路径:{v.Blocks[i].Path} | 大小:{v.Blocks[i].DataSize}");
            }
        }
    }

    private string GetSetting(string key)
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

    private void SaveConfig()
    {
        Configuration cfa = ConfigurationManager.OpenExeConfiguration(ConfigurationUserLevel.None);
        cfa.AppSettings.Settings["OldRootPath"].Value = _oldRootPath;
        cfa.AppSettings.Settings["NewRootPath"].Value = _newRootPath;
        cfa.AppSettings.Settings["PatchPath"].Value = _patchPath;
        cfa.AppSettings.Settings["PatchTargetRootPath"].Value = _patchTargetRootPath;
        cfa.AppSettings.Settings["CompressType"].Value = ((int)_compressType).ToString();
        cfa.Save();
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