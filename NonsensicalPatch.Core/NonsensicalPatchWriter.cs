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
 * MoveFile / MoveFolder 没有 data，主路径是新路径，后面再写：
 * oldPathLength    2
 * oldPathUTF8Bytes y
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

    private byte[] _removeFile = [0];
    private byte[] _createFile = [1];
    private byte[] _modifyFile = [2];
    private byte[] _removeFolder = [3];
    private byte[] _createFolder = [4];
    private byte[] _moveFile = [5];
    private byte[] _moveFolder = [6];

    private int _runningCount = 0;
    private int _blockCount = 0;

    private Stream _output;
    private MD5 _md5;

    private bool _alreadyRunning;

    public NonsensicalPatchWriter(string oldRootPath, string newRootPath, CompressType compressType, Stream outputStream)
    {
        _oldRootPath = oldRootPath;
        _newRootPath = newRootPath;
        _compressType = compressType;
        _output = outputStream;
        _md5 = MD5.Create();
        _md5.Initialize();
    }

    public async Task<long> RunAsync()
    {
        if (_alreadyRunning)
        {
            AddError("请勿重复运行");
            return 0;
        }
        _alreadyRunning = true;
        if (_output is null)
        {
            AddError($"输出流为空");
            return 0;
        }
        if (!_output.CanSeek)
        {
            AddError($"输出流不可查找");
            return 0;
        }
        if (!_output.CanWrite)
        {
            AddError($"输出流不可写");
            return 0;
        }
        if (string.IsNullOrEmpty(_oldRootPath) || (Directory.Exists(_oldRootPath) == false))
        {
            AddError($"旧项目根目录不正确");
            return 0;
        }
        if (string.IsNullOrEmpty(_newRootPath) || (Directory.Exists(_newRootPath) == false))
        {
            AddError($"新项目根目录不正确");
            return 0;
        }

        var testTempPath = Tools.GetTempFilePath();
        try
        {
            var v = File.Create(testTempPath);
            v.Close();
            File.Delete(testTempPath);
        }
        catch (Exception)
        {
            AddError("无权读写临时文件");
            return 0;
        }

        _runningCount = 0;
        ErrorMessage.Clear();
        _oldRootPath = new DirectoryInfo(_oldRootPath).FullName;
        _newRootPath = new DirectoryInfo(_newRootPath).FullName;
        _oldRootPathLength = TreeDiff.RootPrefixLength(_oldRootPath);
        _newRootPathLength = TreeDiff.RootPrefixLength(_newRootPath);

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
                return 0;
            }
        }

        _md5.TransformFinalBlock([], 0, 0);
        if (_md5.Hash == null)
        {
            throw new Exception("md5生成失败");
        }
        byte[] md5Hash = _md5.Hash;
        var endPos = _output.Position;
        _output.Position = startPos + 18;
        _output.Write(md5Hash, 0, 16);
        _output.Write(BitConverter.GetBytes(_blockCount), 0, 4);
        _output.Position = endPos;

        return endPos;
    }

    private void StartDiff()
    {
        try
        {
            DiffPlan plan = TreeDiff.Build(_oldRootPath, _newRootPath);
            WritePlan(plan);
        }
        catch (Exception e)
        {
            AddError(e.ToString());
        }
        finally
        {
            _runningCount--;
        }
    }

    private void WritePlan(DiffPlan plan)
    {
        foreach (string relative in plan.EarlyRemoveFiles)
        {
            Await(DoWriteTypeAndPathAsync(_removeFile, relative));
        }
        foreach (string relative in plan.CreateFolders)
        {
            Await(DoWriteTypeAndPathAsync(_createFolder, relative));
        }
        foreach (var move in plan.FolderMoves)
        {
            Await(DoWriteMoveAsync(_moveFolder, move.NewRelative, move.OldRelative));
        }
        foreach (var move in plan.FileMoves)
        {
            Await(DoWriteMoveAsync(_moveFile, move.NewRelative, move.OldRelative));
        }
        foreach (string relative in plan.RemoveFiles)
        {
            Await(DoWriteTypeAndPathAsync(_removeFile, relative));
        }
        foreach (string relative in plan.RemoveFolders)
        {
            Await(DoWriteTypeAndPathAsync(_removeFolder, relative));
        }

        foreach (var modify in plan.Modifies)
        {
            WriteModityFileAsync(modify.OldFullPath, modify.NewFullPath);
        }
        foreach (string fullPath in plan.CreateFiles)
        {
            WriteCreateFileAsync(new FileInfo(fullPath));
        }
    }

    private static void Await(Task task)
    {
        task.GetAwaiter().GetResult();
    }

    private async void WriteModityFileAsync(string oldFilePath, string newFilePath)
    {
        _runningCount++;
        try
        {
            var tempPath = Tools.GetTempFilePath();
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
            var tempPath = Tools.GetTempFilePath();
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

    private async Task DoWriteMoveAsync(byte[] typeArray, string newRelativePath, string oldRelativePath)
    {
        var newBytes = Encoding.UTF8.GetBytes(newRelativePath);
        var oldBytes = Encoding.UTF8.GetBytes(oldRelativePath);
        if (newBytes.Length > short.MaxValue || oldBytes.Length > short.MaxValue)
        {
            throw new InvalidOperationException($"路径过长：{oldRelativePath} -> {newRelativePath}");
        }

        await _writeLock.WaitAsync();
        try
        {
            _blockCount++;

            var newLength = (short)newBytes.Length;
            var oldLength = (short)oldBytes.Length;
            Logger.Instance.Log($"{_output.Position} | {(BlockType)typeArray[0]} | {oldRelativePath} -> {newRelativePath}");

            await _output.WriteAsync(typeArray, 0, 1);
            await _output.WriteAsync(BitConverter.GetBytes(newLength), 0, 2);
            await _output.WriteAsync(newBytes, 0, newBytes.Length);
            await _output.WriteAsync(BitConverter.GetBytes(oldLength), 0, 2);
            await _output.WriteAsync(oldBytes, 0, oldBytes.Length);

            _md5.TransformBlock(typeArray, 0, 1, null, 0);
            _md5.TransformBlock(BitConverter.GetBytes(newLength), 0, 2, null, 0);
            _md5.TransformBlock(newBytes, 0, newBytes.Length, null, 0);
            _md5.TransformBlock(BitConverter.GetBytes(oldLength), 0, 2, null, 0);
            _md5.TransformBlock(oldBytes, 0, oldBytes.Length, null, 0);
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
}
