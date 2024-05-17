using ICSharpCode.SharpZipLib.BZip2;
using ICSharpCode.SharpZipLib.GZip;
using System.Security.Cryptography;
using System.Text;

namespace NonsensicalPatch.Core;

/* Value            Length
 * Head:
 * magic            16
 * patchVersion     1
 * compressType     1
 * md5              16
 * blockCount       4
 * 
 * Block:
 * blockType        1
 * pathLength       2
 * pathUTF8Bytes    x
 * [dataLength]     8 or 0
 * [data]           y or 0
 * 
 * File:
 * Head             38
 * Block*n          z
 */

public class NonsensicalPatchWriter
{
    public bool HasError => ErrorMessage.Count != 0;
    public List<string> ErrorMessage = new List<string>();

    private readonly SemaphoreSlim _writeLock = new SemaphoreSlim(1, 1);
    private readonly object _errorMessageLock = new();
    private readonly CancellationTokenSource _cancel = new CancellationTokenSource();

    private string _oldRootPath = string.Empty;
    private string _newRootPath = string.Empty;
    private int _oldRootPathLength;
    private int _newRootPathLength;
    private CompressType _compressType;

    private byte[] _removeFile = new byte[] { 0 };
    private byte[] _createFile = new byte[] { 1 };
    private byte[] _modifyFile = new byte[] { 2 };
    private byte[] _removeFolder = new byte[] { 3 };
    private byte[] _createFolder = new byte[] { 4 };

    private int _runningCount = 0;
    private int _blockCount = 0;

    private Stream _output;
    private MD5 _md5;

    public NonsensicalPatchWriter(string oldRootPath, string newRootPath, CompressType compressType, Stream outputStream)
    {
        _oldRootPath = oldRootPath;
        _newRootPath = newRootPath;
        _compressType = compressType;
        _output = outputStream;
        _md5 = MD5.Create();
        _md5.Initialize();
    }

    public async Task RunAsync()
    {
        if (_output is null)
        {
            AddError($"输出流为空");
            return;
        }
        if (!_output.CanSeek)
        {
            AddError($"输出流不可查找");
            return;
        }
        if (!_output.CanWrite)
        {
            AddError($"输出流不可写");
            return;
        }
        if (string.IsNullOrEmpty(_oldRootPath) || (Directory.Exists(_oldRootPath) == false))
        {
            AddError($"旧项目根目录不正确");
            return;
        }
        if (string.IsNullOrEmpty(_newRootPath) || (Directory.Exists(_newRootPath) == false))
        {
            AddError($"新项目根目录不正确");
            return;
        }

        _runningCount = 0;
        ErrorMessage.Clear();
        _oldRootPathLength = _oldRootPath.Length + 1;
        _newRootPathLength = _newRootPath.Length + 1;

        long startPos = _output.Position;

        await DoWriteHead();

        _runningCount++;
        Thread diff = new Thread(StartDiff);
        diff.Start();

        while (_runningCount > 0)
        {
            await Task.Delay(100);
            if (HasError)
            {
                _cancel.Cancel();
                return;
            }
        }

        _md5.TransformFinalBlock([], 0, 0);
        byte[] md5Hash = _md5.Hash;
        var endPos = _output.Position;
        _output.Position = startPos + 18;
        _output.Write(md5Hash, 0, 16);
        _output.Write(BitConverter.GetBytes(_blockCount), 0, 4);
        _output.Position = endPos;
    }

    private void StartDiff()
    {
        Queue<DirectoryInfo> oldDirs = new Queue<DirectoryInfo>();
        oldDirs.Enqueue(new DirectoryInfo(_oldRootPath));
        Queue<DirectoryInfo> newDirs = new Queue<DirectoryInfo>();
        newDirs.Enqueue(new DirectoryInfo(_newRootPath));

        while (oldDirs.Count > 0)
        {
            var oldDir = oldDirs.Dequeue();
            var newDir = newDirs.Dequeue();

            DiffFile(oldDir.EnumerateFiles().ToList(), newDir.EnumerateFiles().ToList());
            if (HasError)
            {
                return;
            }

            var oldChildDirs = oldDir.EnumerateDirectories().ToList();
            var newChildDirs = newDir.EnumerateDirectories().ToList();
            DiffDir(oldChildDirs, newChildDirs);
            if (HasError)
            {
                return;
            }

            foreach (var item in oldChildDirs)
            {
                oldDirs.Enqueue(item);
            }
            foreach (var item in newChildDirs)
            {
                newDirs.Enqueue(item);
            }
        }
        _runningCount--;
    }

    private void DiffFile(List<FileInfo> olfFiles, List<FileInfo> newFiles)
    {
        List<string> newFileNames = new List<string>();

        foreach (var item in newFiles)
        {
            newFileNames.Add(item.Name);
        }

        for (int i = 0; i < olfFiles.Count; i++)
        {
            if (newFileNames.Contains(olfFiles[i].Name))
            {
                int j = newFileNames.IndexOf(olfFiles[i].Name);
                var oldFile= olfFiles[i];
                var newFile= newFiles[j];
                _runningCount++;
                Thread thread = new Thread(() => CompareFile(oldFile, newFile));
                thread.Start();

                olfFiles.RemoveAt(i);
                newFileNames.RemoveAt(j);
                newFiles.RemoveAt(j);
                i--;
            }
        }

        foreach (var item in olfFiles)
        {
            try
            {
                WriteTypeAndPathAsync(_removeFile, item.FullName.Substring(_oldRootPathLength));
            }
            catch (Exception e)
            {
                AddError(e.ToString());
            }
        }

        foreach (var item in newFiles)
        {
            WriteCreateFileAsync(item);
        }
    }

    private void DiffDir(List<DirectoryInfo> olfDirs, List<DirectoryInfo> newDirs)
    {
        List<string> newDirNames = new List<string>();

        HashSet<int> oldSameIndex = new HashSet<int>();
        HashSet<int> newSameIndex = new HashSet<int>();

        //找到相同名称的文件

        foreach (var item in newDirs)
        {
            newDirNames.Add(item.Name);
        }

        for (int i = 0; i < olfDirs.Count; i++)
        {
            if (newDirNames.Contains(olfDirs[i].Name))
            {
                int j = newDirNames.IndexOf(olfDirs[i].Name);

                oldSameIndex.Add(i);
                newSameIndex.Add(j);
            }
        }

        //旧文件夹中没有相同名称的文件夹代表新版本被移除的文件夹

        for (int i = olfDirs.Count - 1; i >= 0; i--)
        {
            if (oldSameIndex.Contains(i) == false)
            {
                WriteTypeAndPathAsync(_removeFolder, olfDirs[i].FullName.Substring(_oldRootPathLength));

                olfDirs.RemoveAt(i);
            }
        }

        //新文件夹中没有相同名称的文件夹代表新版本新增的文件夹

        for (int i = newDirs.Count - 1; i >= 0; i--)
        {
            if (newSameIndex.Contains(i) == false)
            {

                try
                {
                    WriteCreateFolderAsync(newDirs[i]);
                }
                catch (Exception)
                {
                    ErrorMessage.Add($"无法压缩文件夹：{newDirs[i].FullName}");

                    return;
                }

                newDirs.RemoveAt(i);
            }
        }

        //去掉被去除的和新增的文件夹，剩余相同名称的文件夹进入后续循环再做判断
    }

    private async void CompareFile(FileInfo file1, FileInfo file2)
    {
        bool flag = false;
        if (file1.Length != file2.Length)
        {
            flag = true;
        }
        else
        {
            const int BYTES_TO_READ = 1024 * 10;

            using (FileStream fs1 = file1.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream fs2 = file2.Open(FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                byte[] one = new byte[BYTES_TO_READ];
                byte[] two = new byte[BYTES_TO_READ];
                while (true)
                {
                    int len1 = await fs1.ReadAsync(one, 0, BYTES_TO_READ);
                    int len2 = await fs2.ReadAsync(two, 0, BYTES_TO_READ);
                    if (!((ReadOnlySpan<byte>)one).SequenceEqual((ReadOnlySpan<byte>)two)) { flag = true; break; }
                    if (len1 == 0 || len2 == 0) break;  // 有文件读取到了末尾,退出while循环
                }
            }
        }

        if (flag)
        {
            WriteModityFileAsync(file1.FullName, file2.FullName);
        }

        _runningCount--;
    }

    private async void WriteModityFileAsync(string oldFilePath, string newFilePath)
    {
        _runningCount++;
        try
        {
            var tempPath = GetTempFilePath();
            using (var tempFile = File.Create(tempPath))
            {
                var oldPos = tempFile.Position;
                tempFile.Position += 8;
                await Task.Run(() => BinaryPatch.Create(File.ReadAllBytes(oldFilePath), File.ReadAllBytes(newFilePath), tempFile, _compressType));
                var newPos = tempFile.Position;
                var length = newPos - oldPos - 8;
                tempFile.Position = oldPos;
                await tempFile.WriteAsync(BitConverter.GetBytes(length), 0, 8);
                tempFile.Position = newPos;
            }
            await DoWriteTypeAndPathAndFileAsync(_modifyFile, newFilePath.Substring(_newRootPathLength), tempPath);
            Thread diff = new Thread(() => File.Delete(tempPath));
            diff.Start();
        }
        catch (Exception e)
        {
            AddError(e.Message);
            return;
        }
        _runningCount--;
    }

    private async void WriteCreateFileAsync(FileInfo fileInfo)
    {
        _runningCount++;
        try
        {
            var tempPath = GetTempFilePath();
            using (var tempFile = File.Create(tempPath))
            using (var fs = fileInfo.OpenRead())
            {
                var oldPos = tempFile.Position;
                tempFile.Position += 8;
                switch (_compressType)
                {
                    case CompressType.Gzip:
                        await Task.Run(() => GZip.Compress(fs, tempFile, false));
                        break;
                    case CompressType.Bzip2:
                        await Task.Run(() => BZip2.Compress(fs, tempFile, false, 9));
                        break;
                    default:
                        break;
                }
                var newPos = tempFile.Position;
                var length = newPos - oldPos - 8;
                tempFile.Position = oldPos;
                await tempFile.WriteAsync(BitConverter.GetBytes(length), 0, 8);
                tempFile.Position = newPos;
            }
            await DoWriteTypeAndPathAndFileAsync(_createFile, fileInfo.FullName.Substring(_newRootPathLength), tempPath);
            Thread diff = new Thread(() => File.Delete(tempPath));
            diff.Start();
        }
        catch (Exception)
        {
            AddError($"无法读取文件：{fileInfo.FullName}");
            return;
        }
        _runningCount--;
    }

    private async void WriteCreateFolderAsync(DirectoryInfo directoryInfo)
    {
        _runningCount++;
        Queue<DirectoryInfo> dirs = new Queue<DirectoryInfo>();
        dirs.Enqueue(directoryInfo);

        while (dirs.Count > 0)
        {
            var dir = dirs.Dequeue();
            await DoWriteTypeAndPathAsync(_createFolder, dir.FullName.Substring(_newRootPathLength));      //此处等待以保证创建文件夹操作在创建文件之前

            foreach (var item in dir.EnumerateFiles())
            {
                WriteCreateFileAsync(item);
            }
            foreach (var item in dir.EnumerateDirectories())
            {
                dirs.Enqueue(item);
            }
        }
        _runningCount--;
    }

    private async void WriteTypeAndPathAsync(byte[] typeArray, string relativePath)
    {
        _runningCount++;
        await DoWriteTypeAndPathAsync(typeArray, relativePath);
        _runningCount--;
    }

    private async Task DoWriteHead()
    {
        await _writeLock.WaitAsync();
        try
        {
            await _output.WriteAsync(BasicInfo.magic, 0, 16);
            await _output.WriteAsync([BasicInfo.PatchVersion, (byte)_compressType], 0, 2);
            await _output.WriteAsync(new byte[16], 0, 16);   //预留MD5校验码空间
            await _output.WriteAsync(new byte[4], 0, 4);   //预留块数量空间
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task DoWriteTypeAndPathAsync(byte[] typeArray, string relativePath)
    {
        await _writeLock.WaitAsync();
        try
        {
            _blockCount++;
            Logger.Instance.Log($"{_output.Position} | {(BlockType)typeArray[0]} | {relativePath}");
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            var pathLength = (short)pathBytes.Length;
            await _output.WriteAsync(typeArray, 0, 1);
            await _output.WriteAsync(BitConverter.GetBytes(pathLength), 0, 2);
            await _output.WriteAsync(pathBytes, 0, pathBytes.Length);

            _md5.TransformBlock(typeArray, 0, 1, null, 0);
            _md5.TransformBlock(BitConverter.GetBytes(pathLength), 0, 2, null, 0);
            _md5.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task DoWriteTypeAndPathAndFileAsync(byte[] typeArray, string relativePath, string tempFile)
    {
        await _writeLock.WaitAsync();
        try
        {
            _blockCount++;
            Logger.Instance.Log($"{_output.Position} | {(BlockType)typeArray[0]} | {relativePath}");
            var pathBytes = Encoding.UTF8.GetBytes(relativePath);
            var pathLength = (short)pathBytes.Length;
            await _output.WriteAsync(typeArray, 0, 1);
            await _output.WriteAsync(BitConverter.GetBytes(pathLength), 0, 2);
            await _output.WriteAsync(pathBytes, 0, pathBytes.Length);


            _md5.TransformBlock(typeArray, 0, 1, null, 0);
            _md5.TransformBlock(BitConverter.GetBytes(pathLength), 0, 2, null, 0);
            _md5.TransformBlock(pathBytes, 0, pathBytes.Length, null, 0);

            using (var temp = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                bool copying = true;
                var buffer = new byte[4096];
                while (copying)
                {
                    int bytesRead = await temp.ReadAsync(buffer, 0, buffer.Length);
                    if (bytesRead > 0)
                    {
                        _md5.TransformBlock(buffer, 0, bytesRead, null, 0);
                        await _output.WriteAsync(buffer, 0, bytesRead);
                    }
                    else
                    {
                        await _output.FlushAsync();
                        copying = false;
                    }
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private void AddError(string errorMessage)
    {
        lock (_errorMessageLock)
        {
            ErrorMessage.Add(errorMessage);
        }
    }

    private string GetTempFilePath()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), "Nonsensical");
        if (Directory.Exists(tempDir) == false)
        {
            Directory.CreateDirectory(tempDir);
        }
        return Path.Combine(tempDir, Guid.NewGuid().ToString() + ".temp");
    }
}
