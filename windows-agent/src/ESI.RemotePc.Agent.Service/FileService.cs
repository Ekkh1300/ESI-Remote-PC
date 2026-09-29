using System.Net;
using Esi.Platform.Capture;
using Esi.Platform.Files;
using Esi.Platform.Machine;
using Esi.Platform.Telemetry;
using Microsoft.Data.Sqlite;

namespace Esi.Service;

/// <summary>
/// The file manager's business logic. Every path that reaches the filesystem passes
/// through <see cref="FileSandbox"/>, which is the only place a path is allowed to become
/// a real path.
/// </summary>
public sealed class FileService(
    ILogger<FileService> log,
    FileSandbox sandbox,
    SettingsStore settings,
    AuditLog audit,
    AgentDatabase database)
{
    /// <summary>Maximum entries returned by one directory listing.</summary>
    private const int MaxListEntries = 5000;

    /// <summary>
    /// How many subdirectories get a child count in a single listing, and how many children
    /// are counted for each.
    /// <para>
    /// Without a budget, one request to a directory containing N folders costs N times a
    /// full directory enumeration. A profile folder with a few hundred subdirectories
    /// turns a "list this folder" tap into tens of thousands of filesystem stats - slow
    /// enough to look like a hang, and reachable by anyone holding the <c>files</c> scope.
    /// The count is a display nicety, so it is the thing that gets a budget: the first
    /// directories in the listing are counted, the rest report <c>null</c>, and the client
    /// fetches the real number when it opens one.
    /// </para>
    /// </summary>
    private const int MaxChildCountedDirectories = 24;
    private const int MaxChildCountPerDirectory = 200;

    public FileService Initialise()
    {
        settings.ApplyToSandbox(sandbox);
        return this;
    }

    public FileRootListResponse GetRoots()
    {
        var current = settings.Current;

        return new FileRootListResponse
        {
            Roots = sandbox.Roots.Select(r => new FileRootDto
            {
                Path = r.CanonicalPath,
                Label = r.Label,
                ReadOnly = r.ReadOnly,
                Exists = Directory.Exists(r.CanonicalPath),
            }).ToList(),
            DefaultRoot = sandbox.DefaultRoot?.CanonicalPath,
            AllReadOnly = sandbox.Roots.Count > 0 && sandbox.Roots.All(r => r.ReadOnly),
            AllowDelete = current.Files.AllowDelete,
            AllowUpload = current.Files.AllowUpload,
            MaxUploadBytes = current.Files.MaxUploadBytes,
        };
    }

    public FileListResponse List(string? path, bool showHidden)
    {
        var target = string.IsNullOrWhiteSpace(path)
            ? sandbox.DefaultRoot?.CanonicalPath ?? throw new SandboxViolationException("No file roots are configured.")
            : sandbox.ResolveDirectory(path);

        var parent = Path.GetDirectoryName(target);
        var truncated = false;
        var entries = new List<FileEntryDto>();

        try
        {
            var count = 0;

            // Directories are listed first (the ordering is applied below), so the child-count
            // budget is spent on the entries the user is most likely to look at.
            var childCountBudget = MaxChildCountedDirectories;

            foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos())
            {
                if (++count > MaxListEntries)
                {
                    truncated = true;
                    break;
                }

                if (!showHidden && (info.Attributes & FileAttributes.Hidden) != 0)
                    continue;

                var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
                int? childCount = null;

                if (isDirectory && childCountBudget > 0)
                {
                    childCount = CountChildren(info.FullName);
                    childCountBudget--;
                }

                entries.Add(Translate(info, target, childCount));
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            log.LogInformation("Access denied listing {Path}: {Message}", target, ex.Message);
            throw new FileAccessDeniedException("Access to that folder was denied by Windows.");
        }

        // Directories first, then alphabetical: the order a person expects from a file
        // browser, and it keeps the app's list rendering trivial.
        var ordered = entries
            .OrderByDescending(e => e.IsDirectory)
            .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new FileListResponse
        {
            Path = target,
            Parent = parent is not null && sandbox.IsInside(parent, target) ? parent : SafeParent(parent),
            Entries = ordered,
            Truncated = truncated,
            ReadOnly = sandbox.IsReadOnly(target),
            TotalEntries = ordered.Count,
        };
    }

    private string? SafeParent(string? parent)
    {
        if (parent is null)
            return null;

        try
        {
            return sandbox.IsInside(parent, parent) ? parent : null;
        }
        catch
        {
            return null;
        }
    }

    public FileEntryDto Stat(string path)
    {
        var resolved = sandbox.Resolve(path, mustExist: true);

        if (Directory.Exists(resolved))
            return Translate(new DirectoryInfo(resolved), Path.GetDirectoryName(resolved) ?? resolved);

        if (File.Exists(resolved))
            return Translate(new FileInfo(resolved), Path.GetDirectoryName(resolved) ?? resolved);

        throw new SandboxViolationException($"The path does not exist: {resolved}");
    }

    public FileOperationResponse CreateDirectory(string path)
    {
        var target = sandbox.Resolve(path, forWrite: true);
        Directory.CreateDirectory(target);

        RecordAccess(target, true, 0);
        log.LogInformation("Created directory {Path}", target);

        return new FileOperationResponse { Ok = true, Path = target };
    }

    public FileOperationResponse Rename(string from, string to)
    {
        var source = sandbox.Resolve(from, forWrite: true, mustExist: true);
        var destination = sandbox.Resolve(to, forWrite: true);

        if (!settings.Current.Files.AllowDelete)
            return new FileOperationResponse { Ok = false, Detail = "Rename is disabled by configuration." };

        if (Directory.Exists(source))
        {
            if (sandbox.CanDeleteDirectory(source))
                Directory.Move(source, destination);
            else
                throw new FileAccessDeniedException("That folder is the root of an allowed area and cannot be renamed.");
        }
        else if (File.Exists(source))
        {
            File.Move(source, destination);
        }
        else
        {
            throw new SandboxViolationException($"The path does not exist: {source}");
        }

        log.LogInformation("Renamed {Source} to {Destination}", source, destination);
        return new FileOperationResponse { Ok = true, Path = destination };
    }

    public FileOperationResponse Move(string from, string to)
        => Rename(from, to);

    public FileOperationResponse Delete(string path, string? deviceId)
    {
        var current = settings.Current;
        if (!current.Files.AllowDelete)
            return new FileOperationResponse { Ok = false, Detail = "Deleting is disabled in the agent settings." };

        var target = sandbox.Resolve(path, forWrite: true, mustExist: true);

        if (Directory.Exists(target))
        {
            if (!sandbox.CanDeleteDirectory(target))
            {
                audit.Record(AuditLog.ActionFileDenied, "denied", deviceId, remoteAddress: null,
                    detail: $"Refused to delete the sandbox root or an ancestor of one: {target}");

                return new FileOperationResponse
                {
                    Ok = false,
                    Detail = "That folder is an allowed file root, or contains one, so it cannot be deleted.",
                };
            }

            Directory.Delete(target, recursive: true);
        }
        else if (File.Exists(target))
        {
            File.Delete(target);
        }
        else
        {
            throw new SandboxViolationException($"The path does not exist: {target}");
        }

        audit.Record(AuditLog.ActionFileDeleted, "ok", deviceId, detail: target);
        log.LogInformation("Deleted {Path}", target);

        return new FileOperationResponse { Ok = true, Path = target };
    }

    public (Stream Content, long Length, string FileName) OpenDownload(string path)
    {
        var target = sandbox.ResolveFile(path, mustExist: true);
        var stream = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, useAsync: true);
        return (stream, stream.Length, Path.GetFileName(target));
    }

    public UploadResultResponse WriteUpload(string path, Stream source, long expectedSize, string? deviceId)
    {
        var current = settings.Current;

        if (!current.Files.AllowUpload)
            throw new FileAccessDeniedException("Uploading is disabled in the agent settings.");

        if (expectedSize > current.Files.MaxUploadBytes)
            throw new FileAccessDeniedException(
                $"That file is larger than the configured limit ({current.Files.MaxUploadBytes / (1024 * 1024)} MB).");

        var target = sandbox.Resolve(path, forWrite: true);

        // Refuse to clobber a directory, and refuse if the target already exists as a
        // directory. Overwriting a file is allowed: that is what "replace" means.
        if (Directory.Exists(target))
            throw new FileAccessDeniedException("A folder already exists at that path.");

        var directory = Path.GetDirectoryName(target);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        long written;
        byte[] hash;

        using (var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            var buffer = new byte[64 * 1024];
            written = 0;

            int read;
            while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
            {
                written += read;
                if (written > current.Files.MaxUploadBytes)
                {
                    output.Close();
                    SafeDelete(target);
                    throw new FileAccessDeniedException("The upload exceeded the configured size limit.");
                }

                sha.TransformBlock(buffer, 0, read, null, 0);
                output.Write(buffer, 0, read);
            }

            sha.TransformFinalBlock([], 0, 0);
            hash = sha.Hash!;
        }

        RecordAccess(target, false, written);

        log.LogInformation("Received {Bytes} bytes into {Path}", written, target);
        audit.Record(AuditLog.ActionUploadTicket, "ok", deviceId, detail: $"{target} ({written} bytes)");

        return new UploadResultResponse
        {
            Completed = true,
            Path = target,
            BytesWritten = written,
            Sha256 = Convert.ToHexStringLower(hash),
        };
    }

    private static void SafeDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Best effort: the upload limit was hit, so cleaning up is a courtesy.
        }
    }

    public FileSearchResponse Search(string? root, string query, int maxResults)
    {
        var start = string.IsNullOrWhiteSpace(root)
            ? sandbox.DefaultRoot?.CanonicalPath ?? throw new SandboxViolationException("No file roots are configured.")
            : sandbox.ResolveDirectory(root);

        if (string.IsNullOrWhiteSpace(query))
            return new FileSearchResponse { Root = start };

        maxResults = Math.Clamp(maxResults, 1, 500);
        var results = new List<FileEntryDto>();
        var scanned = 0;

        try
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                MaxRecursionDepth = 8,
                IgnoreInaccessible = true,
                AttributesToSkip = FileAttributes.System,
            };

            foreach (var info in new DirectoryInfo(start).EnumerateFileSystemInfos("*", options))
            {
                if (++scanned > 20000)
                    break; // hard cap so a search cannot pin a core indefinitely

                if (info.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(Translate(info, start));
                    if (results.Count >= maxResults)
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Search under {Root} stopped early", start);
        }

        return new FileSearchResponse
        {
            Root = start,
            Entries = results,
            Truncated = results.Count >= maxResults,
            Scanned = scanned,
        };
    }

    public FileListResponse Recent(int limit)
    {
        var entries = new List<FileEntryDto>();

        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, name, is_directory, size_bytes, last_modified
              FROM recent_files ORDER BY accessed_at DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            var path = (string)reader["path"];

            // A recent entry can outlive the file it pointed at.
            if (!File.Exists(path) && !Directory.Exists(path))
                continue;

            // And it must still be inside the sandbox: a root could have been removed
            // from the configuration since the entry was recorded.
            try
            {
                sandbox.Resolve(path, mustExist: false);
            }
            catch (Exception)
            {
                continue;
            }

            entries.Add(new FileEntryDto
            {
                Path = path,
                Name = (string)reader["name"],
                IsDirectory = Convert.ToInt32(reader["is_directory"]) != 0,
                SizeBytes = Convert.ToInt64(reader["size_bytes"]),
                LastModified = reader["last_modified"] is DBNull ? 0 : Convert.ToInt64(reader["last_modified"]),
                CanDelete = false,
                CanRename = false,
            });
        }

        return new FileListResponse { Path = "recent", Entries = entries, TotalEntries = entries.Count };
    }

    private void RecordAccess(string path, bool isDirectory, long size)
    {
        try
        {
            using var connection = database.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO recent_files (path, name, is_directory, size_bytes, last_modified, accessed_at)
                VALUES ($path, $name, $dir, $size, $modified, $now)
                ON CONFLICT(path) DO UPDATE SET
                    name = excluded.name,
                    is_directory = excluded.is_directory,
                    size_bytes = excluded.size_bytes,
                    last_modified = excluded.last_modified,
                    accessed_at = excluded.accessed_at;
                """;
            command.Parameters.AddWithValue("$path", path);
            command.Parameters.AddWithValue("$name", Path.GetFileName(path));
            command.Parameters.AddWithValue("$dir", AgentDatabase.ToDbBool(isDirectory));
            command.Parameters.AddWithValue("$size", size);
            command.Parameters.AddWithValue("$modified", isDirectory ? 0 : File.GetLastWriteTimeUtc(path).Ticks / TimeSpan.TicksPerSecond);
            command.Parameters.AddWithValue("$now", AgentDatabase.NowUnix());
            command.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            log.LogDebug(ex, "Could not record the recent-file entry for {Path}", path);
        }
    }

    /// <param name="childCount">
    /// Pre-computed child count, or <c>null</c> to skip the enumeration. Callers pass a
    /// budgeted value so a single listing cannot turn into thousands of directory reads.
    /// </param>
    private FileEntryDto Translate(FileSystemInfo info, string parent, int? childCount = null)
    {
        var isDirectory = (info.Attributes & FileAttributes.Directory) != 0;
        var path = info.FullName;
        var readOnly = sandbox.IsReadOnly(path);

        return new FileEntryDto
        {
            Name = info.Name,
            Path = path,
            IsDirectory = isDirectory,
            SizeBytes = isDirectory ? 0 : ((FileInfo)info).Length,
            LastModified = info.LastWriteTimeUtc.Ticks / TimeSpan.TicksPerSecond,
            Created = info.CreationTimeUtc.Ticks / TimeSpan.TicksPerSecond,
            IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
            IsReadOnly = readOnly || (info.Attributes & FileAttributes.ReadOnly) != 0,
            ChildCount = isDirectory ? childCount : null,
            Extension = isDirectory ? null : info.Extension.TrimStart('.').ToLowerInvariant(),
            MimeType = isDirectory ? null : GuessMimeType(info.Extension),
            CanDelete = !readOnly && settings.Current.Files.AllowDelete,
            CanRename = !readOnly,
        };
    }

    /// <summary>
    /// Counts children up to a fixed cap. The cap means the number is a lower bound for a
    /// large folder, which is the honest answer: the app uses it to decide whether to offer a
    /// "browse contents" shortcut, not to render an exact total.
    /// </summary>
    private static int? CountChildren(string path)
    {
        try
        {
            return Directory.EnumerateFileSystemEntries(path).Take(MaxChildCountPerDirectory).Count();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A small extension-to-MIME table. Deliberately not a full database: the phone only
    /// needs enough to decide whether to preview an image, a video, audio, or text, and it
    /// re-checks the actual bytes.
    /// </summary>
    public static string? GuessMimeType(string extension) => extension.ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".png" => "image/png",
        ".gif" => "image/gif",
        ".webp" => "image/webp",
        ".bmp" => "image/bmp",
        ".heic" => "image/heic",
        ".svg" => "image/svg+xml",
        ".mp4" => "video/mp4",
        ".mkv" => "video/x-matroska",
        ".webm" => "video/webm",
        ".avi" => "video/x-msvideo",
        ".mov" => "video/quicktime",
        ".mp3" => "audio/mpeg",
        ".flac" => "audio/flac",
        ".wav" => "audio/wav",
        ".m4a" => "audio/mp4",
        ".ogg" => "audio/ogg",
        ".pdf" => "application/pdf",
        ".txt" or ".log" or ".md" => "text/plain",
        ".json" => "application/json",
        ".xml" => "application/xml",
        ".zip" => "application/zip",
        ".7z" => "application/x-7z-compressed",
        ".rar" => "application/vnd.rar",
        _ => "application/octet-stream",
    };
}
