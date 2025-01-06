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
    public bool HasError;
    public Action<MissionState>? MissionStateChanged;
    public PatchInfo PatchInfo { get; private set; }

    private readonly object _errorMessageLock = new();

    private HttpClient _client;
    private int _runningCount;
    private string _patchUrl;
    private string? _targetDirPath;

    private string _tempPatchPath;

    private long _currentMissionMaxSize;

    private bool _alreadyRunning;

    public NonsensicalPatchReader(string patchUrl, string targetDirPath) : this(patchUrl)
    {
        _targetDirPath = targetDirPath;
    }

    public NonsensicalPatchReader(string patchUrl)
    {
        _patchUrl = patchUrl;
        PatchInfo = new PatchInfo();
        _client = new HttpClient(new HttpClientHandler() { UseDefaultCredentials = true });
        _client.DefaultRequestHeaders.Connection.Add("keep-alive");
    }

    public async Task ReadAsync()
    {
        if (_alreadyRunning)
        {
            AddError("请勿重复运行");
            return;
        }
        _alreadyRunning = true;
        if (string.IsNullOrEmpty(_patchUrl))
        {
            AddError("补丁url为空");
            return;
        }
        MissionStateChanged?.Invoke(new MissionState("开始读取补丁"));
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
        if (_alreadyRunning)
        {
            AddError("请勿重复运行");
            return;
        }
        _alreadyRunning = true;
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

        var testTempPath = Tools.GetTempFilePath();
        try
        {
            var v = File.Create(testTempPath);
            v.Close();
            File.Delete(testTempPath);
        }
        catch (Exception e)
        {
            AddError($"无法读写临时文件：{e.Message},{e.StackTrace}");
            return;
        }

        PatchInfo = new PatchInfo();
        Logger.Instance.Log($"补丁：{_patchUrl}");

        using (var patchStream = await GetStream())
        {
            if (patchStream == null)
            {
                AddError("无法加载补丁文件");
                return;
            }
            MissionStateChanged?.Invoke(new MissionState("验证补丁文件"));
            await Verify(patchStream);
            if (HasError)
            {
                return;
            }
            MissionStateChanged?.Invoke(new MissionState("开始应用补丁"));
            await StartPatchAsync(patchStream);
        }
        if (string.IsNullOrEmpty(_tempPatchPath)==false)
        {
            File.Delete(_tempPatchPath);
            _tempPatchPath = string.Empty;
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
        if (_targetDirPath == null)
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

            MissionStateChanged?.Invoke(new MissionState("应用补丁", true, patchStream.Position, _currentMissionMaxSize));
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
                            string tempPatchPath = Tools.GetTempFilePath();
                            using (var tempPatchStream = new FileStream(tempPatchPath, FileMode.Create))
                                await CopyStreamAsync(patchStream, tempPatchStream, new byte[65536], size);

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
                        string tempPatchPath = Tools.GetTempFilePath();
                        using (var tempPatchStream = new FileStream(tempPatchPath, FileMode.Create))
                            await CopyStreamAsync(patchStream, tempPatchStream, new byte[65536], size);
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
        MissionStateChanged?.Invoke(new MissionState("补丁应用完毕"));
    }

    private async Task<Stream?> GetStream()
    {
        Stream patchStream;
        if (File.Exists(_patchUrl))
        {
            try
            {
                MissionStateChanged?.Invoke(new MissionState("读取本地补丁文件"));
                patchStream = File.OpenRead(_patchUrl);
                _currentMissionMaxSize = new FileInfo(_patchUrl).Length; 
            }
            catch (Exception e)
            {
                AddError($"无法读取文件：{_patchUrl},{e.Message},{e.StackTrace}");
                return null;
            }
        }
        else
        {
            MissionStateChanged?.Invoke(new MissionState("从互联网下载补丁"));
            AddError($"未检测到本地补丁文件，尝试从互联网获取", false);
            var response = await GetFileFormInternet(_patchUrl);
            if (response == null)
            {
                AddError($"无法从：{_patchUrl} 获取补丁文件");
                return null;
            }
            _currentMissionMaxSize = response.Content.Headers.ContentLength ?? 0;

            _tempPatchPath = Tools.GetTempFilePath();

            using (var file=File.Create(_tempPatchPath)) 
            using (var download = await response.Content.ReadAsStreamAsync()) 
            {
                var buffer = new byte[65536];

                long totalBytesRead = 0;

                int bytesRead;

                while ((bytesRead = await download.ReadAsync(buffer, 0, buffer.Length).ConfigureAwait(false)) != 0)
                {
                    await file.WriteAsync(buffer, 0, bytesRead).ConfigureAwait(false);
                    totalBytesRead += bytesRead;

                    MissionStateChanged?.Invoke(new MissionState("下载补丁",true, totalBytesRead, _currentMissionMaxSize));
                }
                file.Flush();
                file.Close();
            }
            patchStream = File.OpenRead(_tempPatchPath);
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
            string tempFilePath = Tools.GetTempFilePath();
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

        HttpResponseMessage? response;
        try
        {
            response = await _client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        }
        catch (Exception e)
        {

            AddError($"下载失败：{url},{e.Message},{e.StackTrace}");
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
            MissionStateChanged?.Invoke(new MissionState("应用补丁", true, source.Position, _currentMissionMaxSize));
        }
    }

    private void AddError(string errorMessage, bool error = true)
    {
        lock (_errorMessageLock)
        {
            ErrorMessage.Add(errorMessage);
            if (error)
            {
                HasError = true;
            }
        }
    }
}
