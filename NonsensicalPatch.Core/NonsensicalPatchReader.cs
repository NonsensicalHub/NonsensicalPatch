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

public class NonsensicalPatchReader
{
    public List<string> ErrorMessage = new List<string>();
    public bool HasError => ErrorMessage.Count != 0;
    public PatchInfo PatchInfo { get; private set; }

    private readonly object _errorMessageLock = new();

    private int _runningCount;
    private string _patchUrl;
    private string? _targetDirPath;

    public NonsensicalPatchReader(string patchUrl, string targetDirPath)
    {
        _patchUrl = patchUrl;
        _targetDirPath = targetDirPath;
        PatchInfo = new PatchInfo();
        PatchInfo.Blocks = new List<PatchBlock>();
    }
    
    public NonsensicalPatchReader(string patchUrl)
    {
        _patchUrl = patchUrl;
        PatchInfo = new PatchInfo();
        PatchInfo.Blocks = new List<PatchBlock>();
    }

    public async Task ReadAsync()
    {
        if (string.IsNullOrEmpty(_patchUrl))
        {
            AddError("补丁url为空");
            return;
        }

        using (var patchStream = await GetStream())
        {
            if (patchStream == null)
            {
                AddError("无法加载补丁文件");
                return;
            }
            BinaryReader reader = new BinaryReader(patchStream);
            var magic = reader.ReadBytes(16);
            if (magic.SequenceEqual(BasicInfo.magic) == false)
            {
                AddError($"文件头错误");
                return;
            }
            byte version = reader.ReadByte();
            CompressType compressType = (CompressType)reader.ReadByte();
            byte[] md5Hash = reader.ReadBytes(16);
            int blockCount = reader.ReadInt32();

            PatchInfo.Version = version;
            PatchInfo.CompressType = compressType;
            PatchInfo.MD5Hash = md5Hash;
            PatchInfo.BlockCount = blockCount;

            for (int i = 0; i < PatchInfo.BlockCount; i++)
            {
                BlockType patchType = (BlockType)reader.ReadByte();
                short pathLength = reader.ReadInt16();
                byte[] pathBytes = reader.ReadBytes(pathLength);
                string path = Encoding.UTF8.GetString(pathBytes);

                var newBlock = new PatchBlock(patchType, path);

                switch (patchType)
                {
                    case BlockType.CreateFile:
                    case BlockType.ModifyFile:
                        {
                            long size = reader.ReadInt64();
                            newBlock.DataSize = size;
                            patchStream.Position += size;
                        }
                        break;
                    default:
                        break;
                }
                PatchInfo.Blocks.Add(newBlock);
            }
        }
    }

    public async Task RunAsync()
    {
        if (string.IsNullOrEmpty(_patchUrl))
        {
            AddError("补丁url为空");
            return;
        }
        if (string.IsNullOrEmpty(_targetDirPath) || (Directory.Exists(_targetDirPath) == false))
        {
            AddError("目标路径不可用");
            return;
        }
        PatchInfo = new PatchInfo();
        PatchInfo.Blocks = new List<PatchBlock>();
        Logger.Instance.Log($"补丁：{_patchUrl}");

        using (var patchStream = await GetStream())
        {
            if (patchStream == null)
            {
                AddError("无法加载补丁文件");
                return;
            }
            await Verify(patchStream);
            if (HasError)
            {
                return;
            }
            await StartPatchAsync(patchStream);
        }

        while (_runningCount > 0)
        {
            await Task.Delay(10);
            if (HasError)
            {
                return;
            }
        }

        Logger.Instance.Log("补丁安装完成!");
    }

    private async Task Verify(Stream patchStream)
    {
        BinaryReader reader = new BinaryReader(patchStream);
        var magic = reader.ReadBytes(16);
        if (magic.SequenceEqual(BasicInfo.magic) == false)
        {
            AddError($"文件头错误");
            return;
        }

        byte version = reader.ReadByte();
        if (version < BasicInfo.PatchVersion)
        {
            AddError($"版本过低：{version}");
            return;
        }
        CompressType compressType = (CompressType)reader.ReadByte();
        byte[] md5Hash = reader.ReadBytes(16);
        int blockCount = reader.ReadInt32();

        PatchInfo.Version = version;
        PatchInfo.CompressType = compressType;
        PatchInfo.MD5Hash = md5Hash;
        PatchInfo.BlockCount = blockCount;

        var hash = await MD5.HashDataAsync(patchStream);
        if (hash.SequenceEqual(md5Hash) == false)
        {
            AddError($"md5校验失败");
            return;
        }
    }

    private async Task StartPatchAsync(Stream patchStream)
    {
        if (_targetDirPath==null)
        {
            throw new Exception("_targetDirPath is null");
        }
        patchStream.Position = 0;
        patchStream.Seek(38, SeekOrigin.Current);
        BinaryReader reader = new BinaryReader(patchStream);
        for (int i = 0; i < PatchInfo.BlockCount; i++)
        {
            BlockType patchType = (BlockType)reader.ReadByte();
            short pathLength = reader.ReadInt16();
            byte[] pathBytes = reader.ReadBytes(pathLength);
            string path = Encoding.UTF8.GetString(pathBytes);
            string fullPath = Path.Combine(_targetDirPath, path);

            Logger.Instance.Log($"{patchType} | {path}");

            var newBlock = new PatchBlock(patchType, path);

            switch (patchType)
            {
                case BlockType.RemoveFile:
                    File.Delete(fullPath);
                    break;
                case BlockType.CreateFile:
                    {
                        long size = reader.ReadInt64();
                        newBlock.DataSize = size;
                        var targetPos = patchStream.Position + size;
                        try
                        {
                            string tempPatchPath = GetTempFilePath();
                            using (var tempPatchStream = new FileStream(tempPatchPath, FileMode.Create))
                                await CopyStreamAsync(patchStream, tempPatchStream, new byte[4096], size);
                            _runningCount++;
                            Thread thread = new Thread(() => DecompressFile(tempPatchPath, fullPath));
                            thread.Start();
                        }
                        catch (Exception e)
                        {
                            AddError($"无法创建文件：{fullPath},{e.Message},{e.StackTrace}");
                            return;
                        }
                    }
                    break;
                case BlockType.ModifyFile:
                    {
                        int size = (int)reader.ReadInt64();
                        newBlock.DataSize = size;
                        string tempPatchPath = GetTempFilePath();
                        using (var tempPatchStream = new FileStream(tempPatchPath, FileMode.Create))
                            await CopyStreamAsync(patchStream, tempPatchStream, new byte[4096], size);
                        _runningCount++;
                        Thread thread = new Thread(() => PatchFile(tempPatchPath, fullPath));
                        thread.Start();
                    }
                    break;
                case BlockType.RemoveFolder:
                    Directory.Delete(fullPath, true);
                    break;
                case BlockType.CreateFolder:
                    {
                        Directory.CreateDirectory(fullPath);
                    }
                    break;
                default:
                    break;
            }
            PatchInfo.Blocks.Add(newBlock);
        }
    }

    private async Task<Stream?> GetStream()
    {
        Stream patchStream ;
        if (File.Exists(_patchUrl))
        {
            try
            {
                patchStream = File.OpenRead(_patchUrl);
            }
            catch (Exception)
            {
                AddError($"无法读取文件： {_patchUrl}");
                return null;
            }
        }
        else
        {
            var response = await GetFileFormInternet(_patchUrl);
            if (response == null)
            {
                AddError($"无法从：{_patchUrl} 获取补丁文件");
                return null;
            }
            patchStream = await response.Content.ReadAsStreamAsync();
        }
        return patchStream;
    }

    private void DecompressFile(string tempDataPath, string targetPath)
    {
        try
        {
            using (var patchStream = new FileStream(tempDataPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                switch (PatchInfo.CompressType)
                {
                    case CompressType.Gzip:
                        using (var fs = new FileStream(targetPath, FileMode.Create))
                            GZip.Decompress(patchStream, fs, false);
                        break;
                    case CompressType.Bzip2:
                        using (var fs = new FileStream(targetPath, FileMode.Create))
                            BZip2.Decompress(patchStream, fs, false);
                        break;
                    default:
                        AddError($"未知压缩格式：{PatchInfo.CompressType}");
                        return;
                }
            }
            File.Delete(tempDataPath);
            _runningCount--;
        }
        catch (Exception e)
        {
            AddError($"无法解压文件到：{targetPath},{e}");
            return;
        }
    }

    private void PatchFile(string tempPatchPath, string targetPath)
    {
        try
        {
            string tempFilePath = GetTempFilePath();
            File.Copy(targetPath, tempFilePath);

            using (var fs = new FileStream(tempFilePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var fs2 = new FileStream(targetPath, FileMode.Create))
            {
                BinaryPatch.Apply(fs, () => new FileStream(tempPatchPath, FileMode.Open, FileAccess.Read, FileShare.Read), fs2, PatchInfo.CompressType);
            }
            File.Delete(tempFilePath);
            File.Delete(tempPatchPath);
            _runningCount--;
        }
        catch (Exception e)
        {
            AddError($"无法修改文件到：{targetPath},{e}");
            return;
        }
    }

    private async Task<HttpResponseMessage?> GetFileFormInternet(string url)
    {
        Logger.Instance.Log($"开始下载：{url}");
        var myClient = new HttpClient(new HttpClientHandler() { UseDefaultCredentials = true });
        HttpResponseMessage? response;
        try
        {
            response = await myClient.GetAsync(url);
        }
        catch (Exception)
        {

            AddError($"下载失败：{url}");
            return null;
        }

        Logger.Instance.Log($"下载完成：{url}");
        if (response != null && response.IsSuccessStatusCode)
        {
            return response;
        }
        else
        {
            return null;
        }
    }

    private async Task CopyStreamAsync(Stream source, Stream destination, byte[] buffer, long inputLength)
    {
        bool copying = true;

        long targetPos = source.Position + inputLength;

        while (copying)
        {
            int needReadLength = (int)Math.Min(targetPos - source.Position, buffer.Length);
            int bytesRead = await source.ReadAsync(buffer, 0, needReadLength);
            if (bytesRead != 0)
            {
                await destination.WriteAsync(buffer, 0, bytesRead);
            }
            else
            {
                await destination.FlushAsync();
                copying = false;
            }
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
