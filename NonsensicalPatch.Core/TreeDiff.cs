using System.Security.Cryptography;

namespace NonsensicalPatch.Core;

/// <summary>
/// 对比两棵目录树。内容完全相同的文件或整棵子目录记为移动，其余仍是增删改。
/// </summary>
internal static class TreeDiff
{
    // Windows 上只改大小写仍然是同一个路径。
    private static readonly StringComparer PathKeyComparer =
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly StringComparison PathKeyComparison =
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static int RootPrefixLength(string rootFull)
    {
        return rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull.Length : rootFull.Length + 1;
    }

    internal static DiffPlan Build(string oldRoot, string newRoot)
    {
        DiskTree oldTree = Scan(oldRoot);
        DiskTree newTree = Scan(newRoot);

        var same = new List<(FileEntry Old, FileEntry New)>();
        var oldOnly = new List<FileEntry>();
        var newOnly = new List<FileEntry>();

        foreach (var pair in oldTree.Files)
        {
            if (newTree.Files.TryGetValue(pair.Key, out FileEntry? newer))
            {
                same.Add((pair.Value, newer));
            }
            else
            {
                oldOnly.Add(pair.Value);
            }
        }

        foreach (var pair in newTree.Files)
        {
            if (oldTree.Files.ContainsKey(pair.Key) == false)
            {
                newOnly.Add(pair.Value);
            }
        }

        var plan = new DiffPlan();
        object gate = new();
        Parallel.ForEach(same, pair =>
        {
            if (FilesAreDifferent(pair.Old, pair.New) == false)
            {
                return;
            }

            lock (gate)
            {
                plan.Modifies.Add((pair.Old.FullPath, pair.New.FullPath));
            }
        });
        plan.Modifies.Sort((a, b) => PathKeyComparer.Compare(a.NewFullPath, b.NewFullPath));

        Parallel.ForEach(oldOnly.Concat(newOnly), file => file.Hash = ComputeHash(file.FullPath));

        List<(string OldDir, string NewDir)> folderMoves = MatchDirectoryMoves(oldTree, newTree);
        var movedOldDirs = new HashSet<string>(folderMoves.Select(m => m.OldDir), PathKeyComparer);
        var movedNewDirs = new HashSet<string>(folderMoves.Select(m => m.NewDir), PathKeyComparer);
        plan.FolderMoves.AddRange(folderMoves);

        foreach (var move in MatchFileMoves(oldOnly, newOnly, movedOldDirs, movedNewDirs))
        {
            plan.FileMoves.Add((move.Source.RelativePath, move.Dest.RelativePath));
        }
        plan.FileMoves.Sort((a, b) => PathKeyComparer.Compare(a.OldRelative, b.OldRelative));

        var movedOldFiles = new HashSet<string>(plan.FileMoves.Select(m => m.OldRelative), PathKeyComparer);
        var movedNewFiles = new HashSet<string>(plan.FileMoves.Select(m => m.NewRelative), PathKeyComparer);

        var removeFiles = new List<string>();
        foreach (FileEntry file in oldOnly)
        {
            if (movedOldFiles.Contains(file.RelativePath) || IsUnderAny(file.RelativePath, movedOldDirs))
            {
                continue;
            }
            if (CoveredByFolderDelete(file.RelativePath, newTree.Directories))
            {
                continue;
            }
            removeFiles.Add(file.RelativePath);
        }
        removeFiles.Sort(PathKeyComparer);

        foreach (string relative in removeFiles)
        {
            if (newTree.Directories.Contains(relative))
            {
                plan.EarlyRemoveFiles.Add(relative);
            }
            else
            {
                plan.RemoveFiles.Add(relative);
            }
        }

        foreach (string dir in newTree.Directories.OrderBy(GetDepth).ThenBy(d => d, PathKeyComparer))
        {
            if (oldTree.Directories.Contains(dir) || IsSameOrUnderAny(dir, movedNewDirs))
            {
                continue;
            }
            plan.CreateFolders.Add(dir);
        }

        foreach (string dir in oldTree.Directories.OrderByDescending(GetDepth).ThenBy(d => d, PathKeyComparer))
        {
            if (newTree.Directories.Contains(dir) || IsSameOrUnderAny(dir, movedOldDirs))
            {
                continue;
            }
            string? parent = GetParent(dir);
            if (parent != null && newTree.Directories.Contains(parent) == false)
            {
                continue;
            }
            plan.RemoveFolders.Add(dir);
        }

        foreach (FileEntry file in newOnly.OrderBy(f => f.RelativePath, PathKeyComparer))
        {
            if (movedNewFiles.Contains(file.RelativePath) || IsUnderAny(file.RelativePath, movedNewDirs))
            {
                continue;
            }
            plan.CreateFiles.Add(file.FullPath);
        }

        return plan;
    }

    private static DiskTree Scan(string root)
    {
        var rootInfo = new DirectoryInfo(root);
        string rootFull = rootInfo.FullName;
        int rootLength = RootPrefixLength(rootFull);
        var tree = new DiskTree();
        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootInfo);

        while (pending.Count > 0)
        {
            DirectoryInfo dir = pending.Pop();
            foreach (DirectoryInfo sub in dir.EnumerateDirectories())
            {
                tree.Directories.Add(RelativeOf(sub.FullName, rootFull, rootLength));
                pending.Push(sub);
            }
            foreach (FileInfo file in dir.EnumerateFiles())
            {
                string relative = RelativeOf(file.FullName, rootFull, rootLength);
                tree.Files[relative] = new FileEntry(file.FullName, relative, file.Length);
            }
        }

        return tree;
    }

    private static string RelativeOf(string fullPath, string rootFull, int rootLength)
    {
        if (fullPath.StartsWith(rootFull, StringComparison.Ordinal) == false || fullPath.Length < rootLength)
        {
            throw new InvalidOperationException($"无法计算相对路径：{fullPath}");
        }
        return fullPath.Substring(rootLength);
    }

    private static List<(string OldDir, string NewDir)> MatchDirectoryMoves(DiskTree oldTree, DiskTree newTree)
    {
        var oldRemoved = oldTree.Directories.Where(d => newTree.Directories.Contains(d) == false)
            .OrderBy(GetDepth).ThenBy(d => d, PathKeyComparer).ToList();
        var newAdded = newTree.Directories.Where(d => oldTree.Directories.Contains(d) == false)
            .OrderBy(GetDepth).ThenBy(d => d, PathKeyComparer).ToList();

        var oldSig = new Dictionary<string, string>(PathKeyComparer);
        var newSig = new Dictionary<string, string>(PathKeyComparer);
        foreach (string dir in oldRemoved)
        {
            oldSig[dir] = BuildSignature(dir, oldTree);
        }
        foreach (string dir in newAdded)
        {
            newSig[dir] = BuildSignature(dir, newTree);
        }

        var bySignature = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string dir in newAdded)
        {
            if (bySignature.TryGetValue(newSig[dir], out List<string>? list) == false)
            {
                list = new List<string>();
                bySignature[newSig[dir]] = list;
            }
            list.Add(dir);
        }

        var moves = new List<(string OldDir, string NewDir)>();
        var movedOld = new HashSet<string>(PathKeyComparer);
        foreach (string oldDir in oldRemoved)
        {
            if (movedOld.Any(d => IsUnder(oldDir, d)))
            {
                continue;
            }

            string signature = oldSig[oldDir];
            if (bySignature.TryGetValue(signature, out List<string>? candidates) == false)
            {
                continue;
            }

            var usable = candidates.Where(d => IsUnder(d, oldDir) == false && IsUnder(oldDir, d) == false).ToList();
            string? dest = PickDirDestination(oldDir, signature, usable);
            if (dest == null)
            {
                continue;
            }

            moves.Add((oldDir, dest));
            movedOld.Add(oldDir);
            foreach (string taken in newAdded.Where(d => IsSameOrUnder(d, dest)).ToList())
            {
                if (bySignature.TryGetValue(newSig[taken], out List<string>? list))
                {
                    list.Remove(taken);
                }
            }
        }

        return moves;
    }

    private static string? PickDirDestination(string oldDir, string signature, List<string> candidates)
    {
        if (candidates.Count == 0)
        {
            return null;
        }

        string oldName = GetName(oldDir);
        string? sameName = candidates
            .Where(c => string.Equals(GetName(c), oldName, PathKeyComparison))
            .OrderBy(c => c, PathKeyComparer)
            .FirstOrDefault();
        if (sameName != null)
        {
            return sameName;
        }
        if (signature.Length == 0)
        {
            return null;
        }
        return candidates.OrderBy(c => c, PathKeyComparer).First();
    }

    private static List<(FileEntry Source, FileEntry Dest)> MatchFileMoves(
        List<FileEntry> oldOnly,
        List<FileEntry> newOnly,
        HashSet<string> movedOldDirs,
        HashSet<string> movedNewDirs)
    {
        var sources = oldOnly.Where(f => IsUnderAny(f.RelativePath, movedOldDirs) == false)
            .OrderBy(f => f.RelativePath, PathKeyComparer).ToList();
        var dests = newOnly.Where(f => IsUnderAny(f.RelativePath, movedNewDirs) == false)
            .OrderBy(f => f.RelativePath, PathKeyComparer).ToList();

        var groups = new Dictionary<string, List<FileEntry>>(StringComparer.Ordinal);
        foreach (FileEntry dest in dests)
        {
            string key = ContentKey(dest);
            if (groups.TryGetValue(key, out List<FileEntry>? list) == false)
            {
                list = new List<FileEntry>();
                groups[key] = list;
            }
            list.Add(dest);
        }

        var pairs = new List<(FileEntry Source, FileEntry Dest)>();
        var used = new HashSet<FileEntry>();

        void TryPair(bool sameNameOnly)
        {
            foreach (FileEntry source in sources)
            {
                if (used.Contains(source) || groups.TryGetValue(ContentKey(source), out List<FileEntry>? list) == false)
                {
                    continue;
                }

                FileEntry? match = null;
                foreach (FileEntry dest in list)
                {
                    if (used.Contains(dest))
                    {
                        continue;
                    }
                    if (IsUnder(dest.RelativePath, source.RelativePath) || IsUnder(source.RelativePath, dest.RelativePath))
                    {
                        continue;
                    }
                    if (sameNameOnly && string.Equals(GetName(source.RelativePath), GetName(dest.RelativePath), PathKeyComparison) == false)
                    {
                        continue;
                    }
                    match = dest;
                    break;
                }
                if (match == null)
                {
                    continue;
                }

                used.Add(source);
                used.Add(match);
                pairs.Add((source, match));
            }
        }

        TryPair(true);
        TryPair(false);
        return pairs;
    }

    private static string BuildSignature(string dir, DiskTree tree)
    {
        var lines = new List<string>();
        foreach (FileEntry file in tree.Files.Values)
        {
            if (IsUnder(file.RelativePath, dir) == false)
            {
                continue;
            }
            if (file.Hash == null)
            {
                throw new InvalidOperationException($"未计算文件哈希：{file.RelativePath}");
            }
            string suffix = file.RelativePath[(dir.Length + 1)..];
            lines.Add("F\0" + suffix + "\0" + file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\0" + Convert.ToHexString(file.Hash));
        }
        foreach (string sub in tree.Directories)
        {
            if (IsUnder(sub, dir) == false)
            {
                continue;
            }
            lines.Add("D\0" + sub[(dir.Length + 1)..]);
        }
        lines.Sort(StringComparer.Ordinal);
        return string.Join('\n', lines);
    }

    private static bool FilesAreDifferent(FileEntry oldFile, FileEntry newFile)
    {
        if (oldFile.Length != newFile.Length)
        {
            return true;
        }

        const int bufferSize = 1024 * 10;
        using var fs1 = new FileStream(oldFile.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var fs2 = new FileStream(newFile.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] one = new byte[bufferSize];
        byte[] two = new byte[bufferSize];
        while (true)
        {
            int n1 = fs1.Read(one, 0, bufferSize);
            int n2 = fs2.Read(two, 0, bufferSize);
            if (n1 != n2)
            {
                return true;
            }
            if (n1 == 0)
            {
                return false;
            }
            if (one.AsSpan(0, n1).SequenceEqual(two.AsSpan(0, n2)) == false)
            {
                return true;
            }
        }
    }

    private static byte[] ComputeHash(string fullPath)
    {
        using var md5 = MD5.Create();
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return md5.ComputeHash(stream);
    }

    private static string ContentKey(FileEntry file)
    {
        if (file.Hash == null)
        {
            throw new InvalidOperationException($"未计算文件哈希：{file.RelativePath}");
        }
        return file.Length.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(file.Hash);
    }

    private static bool CoveredByFolderDelete(string relativeFile, HashSet<string> newDirs)
    {
        string? parent = GetParent(relativeFile);
        while (parent != null)
        {
            if (newDirs.Contains(parent) == false)
            {
                return true;
            }
            parent = GetParent(parent);
        }
        return false;
    }

    private static bool IsUnderAny(string path, HashSet<string> dirs)
    {
        foreach (string dir in dirs)
        {
            if (IsUnder(path, dir))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSameOrUnderAny(string path, HashSet<string> dirs)
    {
        foreach (string dir in dirs)
        {
            if (IsSameOrUnder(path, dir))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsSameOrUnder(string path, string dir)
    {
        return string.Equals(path, dir, PathKeyComparison) || IsUnder(path, dir);
    }

    private static bool IsUnder(string path, string dir)
    {
        if (path.Length <= dir.Length || path.StartsWith(dir, PathKeyComparison) == false)
        {
            return false;
        }
        char separator = path[dir.Length];
        return separator == '\\' || separator == '/';
    }

    private static int GetDepth(string relative)
    {
        int depth = 1;
        for (int i = 0; i < relative.Length; i++)
        {
            if (relative[i] == '\\' || relative[i] == '/')
            {
                depth++;
            }
        }
        return depth;
    }

    private static string? GetParent(string relative)
    {
        int index = relative.LastIndexOfAny(['\\', '/']);
        return index < 0 ? null : relative[..index];
    }

    private static string GetName(string relative)
    {
        int index = relative.LastIndexOfAny(['\\', '/']);
        return index < 0 ? relative : relative[(index + 1)..];
    }

    private sealed class FileEntry
    {
        public readonly string FullPath;
        public readonly string RelativePath;
        public readonly long Length;
        public byte[]? Hash;

        public FileEntry(string fullPath, string relativePath, long length)
        {
            FullPath = fullPath;
            RelativePath = relativePath;
            Length = length;
        }
    }

    private sealed class DiskTree
    {
        public readonly Dictionary<string, FileEntry> Files = new(PathKeyComparer);
        public readonly HashSet<string> Directories = new(PathKeyComparer);
    }
}

internal sealed class DiffPlan
{
    public readonly List<(string OldFullPath, string NewFullPath)> Modifies = new();
    public readonly List<(string OldRelative, string NewRelative)> FolderMoves = new();
    public readonly List<(string OldRelative, string NewRelative)> FileMoves = new();
    public readonly List<string> CreateFolders = new();
    public readonly List<string> RemoveFolders = new();
    public readonly List<string> CreateFiles = new();
    public readonly List<string> EarlyRemoveFiles = new();
    public readonly List<string> RemoveFiles = new();
}
