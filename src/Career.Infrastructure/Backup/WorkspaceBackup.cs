using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Career.Contracts;
using Career.Core;
using Microsoft.Data.Sqlite;

namespace Career.Infrastructure.Backup;

public static class WorkspaceBackup
{
    private const int SchemaVersion = 1;
    private const int MaxBackupBytes = 512 * 1024 * 1024;
    private const int MaxManifestBytes = 1024 * 1024;
    private const int MaxEntries = 100_000;
    private const string ManifestName = "manifest.json";
    private const string SecretRestorationRequirement =
        "The encrypted vault is included when present; system-protected master keys are not exported. Cross-machine restores may require the original OS credentials or reconfiguring secrets.";

    public static void Create(string workspaceDirectory, string destinationZip, Action<string> backupDatabase)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationZip);
        ArgumentNullException.ThrowIfNull(backupDatabase);

        string workspaceInput = Path.GetFullPath(workspaceDirectory);
        EnsureNoReparseLeaf(workspaceInput);
        string workspace = ResolveExistingPath(workspaceInput);
        EnsureDirectory(workspace, "工作区");
        string destinationInput = Path.GetFullPath(destinationZip);
        EnsureNoReparseLeaf(destinationInput);
        string destinationDirectoryInput = Path.GetDirectoryName(destinationInput) ?? throw new IOException("备份目标必须位于现有目录中。");
        string destinationDirectory = ResolveExistingPath(destinationDirectoryInput);
        EnsureDirectory(destinationDirectory, "备份目标目录");
        string destination = Path.Combine(destinationDirectory, Path.GetFileName(destinationInput));
        if (IsWithin(workspace, destination))
            throw new IOException("备份 ZIP 必须保存在工作区之外。");
        if (PathExists(destination))
            throw new IOException("备份目标已存在，不会覆盖现有文件。");

        string stageRoot = Path.Combine(destinationDirectory, ".career-backup-" + Guid.NewGuid().ToString("N") + ".tmp");
        string partialZip = Path.Combine(destinationDirectory, "." + Path.GetFileName(destination) + "." + Guid.NewGuid().ToString("N") + ".partial");
        Directory.CreateDirectory(stageRoot);
        SetPrivateDirectory(stageRoot);
        try
        {
            string stagedDatabase = Path.Combine(stageRoot, "state", "app.db");
            Directory.CreateDirectory(Path.GetDirectoryName(stagedDatabase)!);
            SetPrivateDirectory(Path.GetDirectoryName(stagedDatabase)!);
            backupDatabase(stagedDatabase);
            EnsureRegularFile(stagedDatabase, "SQLite 一致副本");

            WorkspaceMetadata metadata = ReadWorkspaceMetadata(Path.Combine(workspace, "workspace.json"));
            ValidateDatabaseWorkspaceId(stagedDatabase, metadata.Id);

            var files = new List<BackupSource>();
            AddRequiredFile(workspace, "workspace.json", files);
            files.Add(new BackupSource(stagedDatabase, "state/app.db"));
            AddOptionalFile(workspace, "state/vault.enc", files);
            AddTree(workspace, ".models", files);
            AddTree(workspace, "artifacts", files);
            if (files.Count > MaxEntries)
                throw new IOException("工作区文件数量超过备份支持上限。");

            var manifestFiles = new List<WorkspaceBackupFile>(files.Count);
            long totalBytes = 0;
            using (var output = new FileStream(partialZip, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                SetPrivateFile(partialZip);
                using (var archive = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true))
                {
                    foreach (BackupSource source in files.OrderBy(file => file.ArchivePath, StringComparer.Ordinal))
                    {
                        ValidateArchivePath(source.ArchivePath);
                        WorkspaceBackupFile file = AddFileToArchive(archive, source);
                        totalBytes = checked(totalBytes + file.Bytes);
                        if (totalBytes > MaxBackupBytes)
                            throw new IOException("工作区备份内容超过 512 MiB 上限。");
                        manifestFiles.Add(file);
                    }

                    var manifest = new WorkspaceBackupManifest(
                        SchemaVersion,
                        Protocol.Version,
                        metadata.Id,
                        DateTimeOffset.UtcNow,
                        SecretRestorationRequirement,
                        manifestFiles.ToArray());
                    byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest, WorkspaceBackupJsonContext.Default.WorkspaceBackupManifest);
                    if (manifestBytes.Length > MaxManifestBytes)
                        throw new IOException("备份清单超过支持上限。");
                    ZipArchiveEntry manifestEntry = archive.CreateEntry(ManifestName, CompressionLevel.Optimal);
                    manifestEntry.ExternalAttributes = (int)FileAttributes.Normal;
                    using Stream entryStream = manifestEntry.Open();
                    entryStream.Write(manifestBytes);
                }

                output.Flush(flushToDisk: true);
                if (output.Length > MaxBackupBytes)
                    throw new IOException("备份 ZIP 超过 512 MiB 上限。");
            }

            File.Move(partialZip, destination, overwrite: false);
        }
        finally
        {
            TryDeleteFile(partialZip);
            TryDeleteDirectory(stageRoot);
        }
    }

    public static void Restore(string sourceZip, string targetEmptyDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceZip);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetEmptyDirectory);

        string sourceInput = Path.GetFullPath(sourceZip);
        EnsureNoReparseLeaf(sourceInput);
        string source = ResolveExistingPath(sourceInput);
        EnsureRegularFile(source, "备份 ZIP");
        var sourceInfo = new FileInfo(source);
        if (sourceInfo.Length > MaxBackupBytes)
            throw new IOException("备份 ZIP 超过 512 MiB 上限。");

        string targetInput = Path.GetFullPath(targetEmptyDirectory);
        EnsureNoReparseLeaf(targetInput);
        string parentInput = Path.GetDirectoryName(targetInput) ?? throw new IOException("恢复目标必须是独立目录。");
        string parent = ResolveExistingPath(parentInput);
        EnsureDirectory(parent, "恢复目标父目录");
        string target = Path.Combine(parent, Path.GetFileName(targetInput));
        EnsureNoReparseLeaf(target);
        bool targetExisted = PathExists(target);
        if (targetExisted)
        {
            if (!Directory.Exists(target))
                throw new IOException("恢复目标已存在且不是空目录。");
            if (!IsDirectoryEmpty(target))
                throw new IOException("恢复目标必须不存在或确实为空；现有数据已保留。");
        }

        string stage = Path.Combine(parent, "." + Path.GetFileName(target) + ".restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        Directory.CreateDirectory(stage);
        SetPrivateDirectory(stage);
        string? heldEmptyTarget = null;
        bool movedToTarget = false;
        try
        {
            WorkspaceBackupManifest manifest = ReadAndValidateArchive(source, stage);
            var marker = new WorkspaceRestoreMarker(SchemaVersion, manifest.WorkspaceId, DateTimeOffset.UtcNow);
            string markerPath = Path.Combine(stage, "state", "restore-pending");
            byte[] markerBytes = JsonSerializer.SerializeToUtf8Bytes(marker, WorkspaceBackupJsonContext.Default.WorkspaceRestoreMarker);
            WriteDurable(markerPath, markerBytes);

            if (targetExisted)
            {
                if (!IsDirectoryEmpty(target))
                    throw new IOException("恢复期间目标目录出现了数据；已有数据已保留。");
                heldEmptyTarget = Path.Combine(parent, "." + Path.GetFileName(target) + ".empty-" + Guid.NewGuid().ToString("N"));
                Directory.Move(target, heldEmptyTarget);
                if (!IsDirectoryEmpty(heldEmptyTarget))
                {
                    TryMoveDirectoryBack(heldEmptyTarget, target);
                    heldEmptyTarget = Directory.Exists(heldEmptyTarget) ? heldEmptyTarget : null;
                    throw new IOException("恢复期间目标目录出现了数据；原目录已保留。");
                }
            }

            try
            {
                Directory.Move(stage, target);
                movedToTarget = true;
            }
            catch
            {
                if (heldEmptyTarget is not null && !PathExists(target))
                {
                    TryMoveDirectoryBack(heldEmptyTarget, target);
                    heldEmptyTarget = Directory.Exists(heldEmptyTarget) ? heldEmptyTarget : null;
                }
                throw;
            }

            if (heldEmptyTarget is not null && IsDirectoryEmpty(heldEmptyTarget))
            {
                try
                {
                    Directory.Delete(heldEmptyTarget, recursive: false);
                    heldEmptyTarget = null;
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Preserve the verified empty directory if cleanup is temporarily unavailable.
                }
            }
        }
        finally
        {
            if (!movedToTarget)
                TryDeleteDirectory(stage);
        }
    }

    private static WorkspaceBackupManifest ReadAndValidateArchive(string source, string stage)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var archive = new ZipArchive(input, ZipArchiveMode.Read, leaveOpen: false);
        if (archive.Entries.Count is < 2 or > MaxEntries + 1)
            throw new IOException("备份 ZIP 条目数量无效。");

        var entryByName = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            ValidateZipEntry(entry);
            if (!entryByName.TryAdd(entry.FullName, entry))
                throw new IOException("备份 ZIP 含有重复或大小写冲突的路径。");
        }

        if (!entryByName.TryGetValue(ManifestName, out ZipArchiveEntry? manifestEntry) || manifestEntry.FullName != ManifestName)
            throw new IOException("备份清单缺失或路径无效。");
        if (manifestEntry.Length > MaxManifestBytes || manifestEntry.CompressedLength > MaxManifestBytes)
            throw new IOException("备份清单超过支持上限。");
        byte[] manifestBytes = ReadEntryBounded(manifestEntry, MaxManifestBytes);
        WorkspaceBackupManifest manifest = JsonSerializer.Deserialize(manifestBytes, WorkspaceBackupJsonContext.Default.WorkspaceBackupManifest)
            ?? throw new IOException("备份清单无法读取。");
        ValidateManifest(manifest);

        if (manifest.Files.Length + 1 != archive.Entries.Count)
            throw new IOException("备份 ZIP 含有清单未声明的文件或缺失文件。");

        var declared = new Dictionary<string, WorkspaceBackupFile>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        foreach (WorkspaceBackupFile file in manifest.Files)
        {
            ValidateArchivePath(file.Path);
            if (!declared.TryAdd(file.Path, file))
                throw new IOException("备份清单包含重复或大小写冲突的路径。");
            declaredBytes = checked(declaredBytes + file.Bytes);
            if (declaredBytes > MaxBackupBytes)
                throw new IOException("备份内容超过 512 MiB 上限。");
            if (!entryByName.TryGetValue(file.Path, out ZipArchiveEntry? entry) || entry.FullName != file.Path)
                throw new IOException("备份 ZIP 缺少清单声明的文件。");
            if (entry.Length != file.Bytes || entry.Length > MaxBackupBytes)
                throw new IOException("备份文件长度与清单不一致。");
        }

        foreach (string name in entryByName.Keys)
        {
            if (name != ManifestName && !declared.ContainsKey(name))
                throw new IOException("备份 ZIP 含有清单未声明的文件。");
        }
        if (!declared.ContainsKey("workspace.json") || !declared.ContainsKey("state/app.db"))
            throw new IOException("备份缺少工作区配置或主数据库。");

        foreach (WorkspaceBackupFile file in manifest.Files)
            ExtractAndVerify(entryByName[file.Path], file, stage);

        WorkspaceMetadata metadata = ReadWorkspaceMetadata(Path.Combine(stage, "workspace.json"));
        if (!string.Equals(metadata.Id, manifest.WorkspaceId, StringComparison.Ordinal))
            throw new IOException("工作区配置与备份清单的 ID 不一致。");
        string databasePath = Path.Combine(stage, "state", "app.db");
        ValidateDatabaseWorkspaceId(databasePath, manifest.WorkspaceId);
        RemoveTransientSqliteSidecars(databasePath);
        return manifest;
    }

    private static void ValidateManifest(WorkspaceBackupManifest manifest)
    {
        if (manifest.SchemaVersion != SchemaVersion || manifest.Protocol != Protocol.Version)
            throw new IOException("备份清单版本与当前程序不兼容。");
        if (string.IsNullOrWhiteSpace(manifest.WorkspaceId) || !manifest.WorkspaceId.StartsWith("workspace_", StringComparison.Ordinal))
            throw new IOException("备份工作区 ID 无效。");
        if (manifest.CreatedAt == default || manifest.Files is null || manifest.Files.Length is < 2 or > MaxEntries)
            throw new IOException("备份清单元数据无效。");
        if (!string.Equals(manifest.SecretRestorationRequirement, SecretRestorationRequirement, StringComparison.Ordinal))
            throw new IOException("备份清单缺少凭据恢复说明。");
        foreach (WorkspaceBackupFile? file in manifest.Files)
        {
            if (file is null || file.Bytes < 0 || string.IsNullOrEmpty(file.Sha256) || file.Sha256.Length != 64 || file.Sha256.Any(character => !char.IsAsciiHexDigit(character)))
                throw new IOException("备份清单包含无效的长度或 SHA-256。");
        }
    }

    private static void ValidateZipEntry(ZipArchiveEntry entry)
    {
        if (string.IsNullOrEmpty(entry.FullName) || entry.FullName.EndsWith("/", StringComparison.Ordinal))
            throw new IOException("备份 ZIP 含有目录或空路径条目。");
        if (entry.FullName != ManifestName)
            ValidateArchivePath(entry.FullName);
        int dosAttributes = entry.ExternalAttributes & 0xFFFF;
        int unixMode = (entry.ExternalAttributes >> 16) & 0xFFFF;
        if (((FileAttributes)dosAttributes & FileAttributes.ReparsePoint) != 0 || (unixMode & 0xF000) == 0xA000)
            throw new IOException("备份 ZIP 含有符号链接或 reparse point。");
        if (entry.Length < 0 || entry.CompressedLength < 0 || entry.Length > 0 && entry.CompressedLength == 0)
            throw new IOException("备份 ZIP 条目长度无效。");
    }

    private static void ValidateArchivePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 1024 || path.StartsWith("/", StringComparison.Ordinal) || path.Contains('\\') || path.Contains('\0'))
            throw new IOException("备份路径无效。");
        string[] segments = path.Split('/');
        if (segments.Any(segment => segment.Length == 0 || segment is "." or ".." || segment.EndsWith(' ') || segment.EndsWith('.')))
            throw new IOException("备份路径包含空段或穿越段。");
        foreach (string segment in segments)
        {
            if (segment.Any(character => char.IsControl(character) || character is '<' or '>' or ':' or '"' or '|' or '?' or '*'))
                throw new IOException("备份路径包含当前平台不可移植的字符。");
            string deviceName = segment.Split('.', 2)[0];
            if (deviceName.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || deviceName.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || deviceName.Length == 4 && (deviceName.StartsWith("COM", StringComparison.OrdinalIgnoreCase) || deviceName.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) && deviceName[3] is >= '1' and <= '9')
                throw new IOException("备份路径包含平台保留名称。");
        }
        bool allowed = path == "workspace.json"
            || path is "state/app.db" or "state/vault.enc"
            || path.StartsWith(".models/", StringComparison.Ordinal)
            || path.StartsWith("artifacts/", StringComparison.Ordinal);
        if (!allowed)
            throw new IOException("备份路径超出工作区白名单。");
    }

    private static void ExtractAndVerify(ZipArchiveEntry entry, WorkspaceBackupFile file, string stage)
    {
        string destination = Path.GetFullPath(Path.Combine(stage, file.Path.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsWithin(stage, destination))
            throw new IOException("备份文件路径超出暂存目录。");
        string parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        EnsureNoReparseComponentsFrom(parent, stage);
        SetPrivateDirectory(parent);
        using Stream compressed = entry.Open();
        using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough);
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = compressed.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > file.Bytes || total > MaxBackupBytes)
                throw new IOException("备份文件实际长度超过清单或支持上限。");
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        output.Flush(flushToDisk: true);
        byte[] actualHash = hash.GetHashAndReset();
        byte[] expectedHash = Convert.FromHexString(file.Sha256);
        if (total != file.Bytes || !CryptographicOperations.FixedTimeEquals(actualHash, expectedHash))
            throw new IOException("备份文件长度或 SHA-256 校验失败。");
        SetPrivateFile(destination);
    }

    private static WorkspaceBackupFile AddFileToArchive(ZipArchive archive, BackupSource source)
    {
        EnsureRegularFile(source.SourcePath, source.ArchivePath);
        ZipArchiveEntry entry = archive.CreateEntry(source.ArchivePath, CompressionLevel.Optimal);
        entry.ExternalAttributes = (int)FileAttributes.Normal;
        using var input = new FileStream(source.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        using Stream output = entry.Open();
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            int read = input.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > MaxBackupBytes)
                throw new IOException("工作区文件超过 512 MiB 上限。");
            hash.AppendData(buffer, 0, read);
            output.Write(buffer, 0, read);
        }
        return new WorkspaceBackupFile(source.ArchivePath, total, Convert.ToHexStringLower(hash.GetHashAndReset()));
    }

    private static void AddRequiredFile(string workspace, string relativePath, List<BackupSource> files)
    {
        string path = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        EnsureNoReparseComponentsFrom(path, workspace);
        EnsureRegularFile(path, relativePath);
        files.Add(new BackupSource(path, relativePath));
    }

    private static void AddOptionalFile(string workspace, string relativePath, List<BackupSource> files)
    {
        string path = Path.Combine(workspace, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (PathExists(path))
        {
            EnsureNoReparseComponentsFrom(path, workspace);
            EnsureRegularFile(path, relativePath);
            files.Add(new BackupSource(path, relativePath));
        }
    }

    private static void AddTree(string workspace, string relativeRoot, List<BackupSource> files)
    {
        string root = Path.Combine(workspace, relativeRoot);
        if (!PathExists(root))
            return;
        EnsureNoReparseComponentsFrom(root, workspace);
        EnsureDirectory(root, relativeRoot);
        Visit(root);

        void Visit(string directory)
        {
            EnsureNoReparseComponentsFrom(directory, workspace);
            EnsureDirectory(directory, directory);
            foreach (string path in Directory.EnumerateFileSystemEntries(directory).OrderBy(item => Path.GetFileName(item), StringComparer.Ordinal))
            {
                EnsureNoReparseComponentsFrom(path, workspace);
                FileAttributes attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.Device) != 0)
                    throw new IOException("备份目录含有不支持的设备文件。");
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    ValidateArchivePath(Path.GetRelativePath(workspace, path).Replace(Path.DirectorySeparatorChar, '/'));
                    Visit(path);
                }
                else
                {
                    EnsureRegularFile(path, path);
                    string archivePath = Path.GetRelativePath(workspace, path).Replace(Path.DirectorySeparatorChar, '/');
                    ValidateArchivePath(archivePath);
                    files.Add(new BackupSource(path, archivePath));
                    if (files.Count > MaxEntries)
                        throw new IOException("工作区文件数量超过备份支持上限。");
                }
            }
        }
    }

    private static WorkspaceMetadata ReadWorkspaceMetadata(string path)
    {
        EnsureRegularFile(path, "workspace.json");
        var info = new FileInfo(path);
        if (info.Length is <= 0 or > 1024 * 1024)
            throw new IOException("workspace.json 长度无效。");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        WorkspaceMetadata metadata = JsonSerializer.Deserialize(stream, StorageJson.Default.WorkspaceMetadata)
            ?? throw new IOException("workspace.json 为空。");
        if (metadata.SchemaVersion != 1 || string.IsNullOrWhiteSpace(metadata.Name) || string.IsNullOrWhiteSpace(metadata.Id) || !metadata.Id.StartsWith("workspace_", StringComparison.Ordinal))
            throw new IOException("workspace.json 版本或工作区 ID 无效。");
        return metadata;
    }

    private static void ValidateDatabaseWorkspaceId(string databasePath, string workspaceId)
    {
        EnsureRegularFile(databasePath, "主数据库");
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        using var connection = new SqliteConnection(connectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM settings WHERE key='workspaceId'";
        if (!string.Equals(command.ExecuteScalar() as string, workspaceId, StringComparison.Ordinal))
            throw new IOException("SQLite 主数据库与 workspace.json 的工作区 ID 不一致。");
    }

    private static void RemoveTransientSqliteSidecars(string databasePath)
    {
        foreach (string suffix in new[] { "-wal", "-shm" })
        {
            string path = databasePath + suffix;
            if (!PathExists(path))
                continue;
            EnsureRegularFile(path, "SQLite 校验副本");
            File.Delete(path);
        }
    }

    private static void EnsureDirectory(string path, string label)
    {
        EnsureNoReparseLeaf(path);
        if (!Directory.Exists(path))
            throw new IOException($"{label}目录不存在。");
        if ((File.GetAttributes(path) & FileAttributes.Directory) == 0)
            throw new IOException($"{label}路径不是目录。");
    }

    private static void EnsureRegularFile(string path, string label)
    {
        EnsureNoReparseLeaf(path);
        if (!File.Exists(path) || (File.GetAttributes(path) & (FileAttributes.Directory | FileAttributes.Device | FileAttributes.ReparsePoint)) != 0)
            throw new IOException($"{label}不存在或不是普通文件。");
    }

    private static void EnsureNoReparseLeaf(string path)
    {
        string fullPath = Path.GetFullPath(path);
        FileSystemInfo info = Directory.Exists(fullPath) ? new DirectoryInfo(fullPath) : new FileInfo(fullPath);
        if (info.LinkTarget is not null || info.Exists && (File.GetAttributes(fullPath) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("路径包含符号链接或 reparse point。");
    }

    private static void EnsureNoReparseComponentsFrom(string path, string safeRoot)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(safeRoot));
        string fullPath = Path.GetFullPath(path);
        if (!IsWithin(root, fullPath))
            throw new IOException("路径超出工作区或暂存目录。");
        EnsureNoReparseLeaf(root);
        string relative = Path.GetRelativePath(root, fullPath);
        if (relative == ".")
            return;
        string current = root;
        foreach (string segment in relative.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            EnsureNoReparseLeaf(current);
        }
    }

    private static string ResolveExistingPath(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? throw new IOException("路径格式无效。");
        string current = root;
        foreach (string segment in fullPath[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(current, segment);
            FileSystemInfo info = Directory.Exists(candidate) ? new DirectoryInfo(candidate) : new FileInfo(candidate);
            string? linkTarget = info.LinkTarget;
            if (linkTarget is null)
            {
                current = candidate;
                continue;
            }
            string resolvedTarget = Path.IsPathRooted(linkTarget)
                ? linkTarget
                : Path.Combine(Path.GetDirectoryName(candidate)!, linkTarget);
            current = Path.GetFullPath(resolvedTarget);
        }
        return Path.TrimEndingDirectorySeparator(current);
    }

    private static bool PathExists(string path)
    {
        FileSystemInfo file = new FileInfo(path);
        if (file.LinkTarget is not null || file.Exists)
            return true;
        FileSystemInfo directory = new DirectoryInfo(path);
        return directory.LinkTarget is not null || directory.Exists;
    }

    private static bool IsDirectoryEmpty(string path)
    {
        EnsureDirectory(path, "恢复目标");
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return !entries.MoveNext();
    }

    private static bool IsWithin(string parent, string candidate)
    {
        string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        string path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        StringComparison comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return path.Equals(root, comparison) || path.StartsWith(root + Path.DirectorySeparatorChar, comparison);
    }

    private static byte[] ReadEntryBounded(ZipArchiveEntry entry, int maximumBytes)
    {
        using Stream stream = entry.Open();
        using var output = new MemoryStream((int)entry.Length);
        byte[] buffer = new byte[16 * 1024];
        int total = 0;
        while (true)
        {
            int read = stream.Read(buffer, 0, buffer.Length);
            if (read == 0)
                break;
            total = checked(total + read);
            if (total > maximumBytes)
                throw new IOException("备份清单超过支持上限。");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static void WriteDurable(string path, byte[] bytes)
    {
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.Write(bytes);
            output.Flush(flushToDisk: true);
        }
        SetPrivateFile(path);
    }

    private static void SetPrivateDirectory(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    private static void SetPrivateFile(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    private static void TryMoveDirectoryBack(string source, string destination)
    {
        try
        {
            if (!PathExists(destination))
                Directory.Move(source, destination);
        }
        catch
        {
            // Keep the empty directory under its unique sibling name if another actor claimed the original path.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (PathExists(path) && !Directory.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // A uniquely named partial file is safe to leave behind if the filesystem rejects cleanup.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Keep failed staging cleanup isolated under its unique sibling name.
        }
    }

    private sealed record BackupSource(string SourcePath, string ArchivePath);
}

internal sealed record WorkspaceBackupManifest(
    int SchemaVersion,
    string Protocol,
    string WorkspaceId,
    DateTimeOffset CreatedAt,
    string SecretRestorationRequirement,
    WorkspaceBackupFile[] Files);

internal sealed record WorkspaceBackupFile(string Path, long Bytes, string Sha256);

internal sealed record WorkspaceRestoreMarker(int SchemaVersion, string WorkspaceId, DateTimeOffset RestoredAt);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorkspaceBackupManifest))]
[JsonSerializable(typeof(WorkspaceRestoreMarker))]
internal partial class WorkspaceBackupJsonContext : JsonSerializerContext;
