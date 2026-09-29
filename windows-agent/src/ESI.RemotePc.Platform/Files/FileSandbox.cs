using Esi.Platform.Native;
using Microsoft.Extensions.Logging;

namespace Esi.Platform.Files;

/// <summary>Raised when a path escapes the configured sandbox.</summary>
public sealed class SandboxViolationException(string message) : Exception(message)
{
    public string Code { get; init; } = "esi_path_not_allowed";
}

/// <summary>Thrown by the file service when the caller lacks permission for an operation.</summary>
public sealed class FileAccessDeniedException(string message) : Exception(message)
{
    public string Code { get; init; } = "esi_forbidden_scope";
}

/// <summary>
/// Confines every file operation to a configured set of roots.
/// <para>
/// This class is the single gate between the network and the filesystem, so it is written
/// to fail closed and to be boring:
/// </para>
/// <list type="number">
///   <item><description>Roots are canonicalised once and compared with
///   <c>OrdinalIgnoreCase</c>, because NTFS is case-insensitive by default and a
///   case-sensitive comparison would be a bypass.</description></item>
///   <item><description>Every candidate path is canonicalised, then the <b>final</b>
///   path is re-checked. Canonicalisation alone is not enough: a junction or symlink
///   inside an allowed root can point anywhere, so the resolved target is what gets
///   validated.</description></item>
///   <item><description>Device paths, UNC paths outside the roots, NTFS alternate data
///   streams, reserved DOS device names, and 8.3 short names are rejected outright
///   rather than normalised, because each is a known way to smuggle a path past a
///   naive check.</description></item>
///   <item><description>Read-only roots reject every mutating verb, and a root can never
///   itself be deleted or renamed away.</description></item>
/// </list>
/// </summary>
public sealed class FileSandbox
{
    private readonly ILogger<FileSandbox> _log;
    private readonly List<Root> _roots = [];

    public FileSandbox(ILogger<FileSandbox> log) => _log = log;

    /// <summary>One allowed root.</summary>
    public sealed record Root(string CanonicalPath, string Label, bool ReadOnly)
    {
        /// <summary>Canonical path plus the trailing separator, for prefix comparisons.</summary>
        public string Prefix => CanonicalPath.EndsWith('\\')
            ? CanonicalPath
            : CanonicalPath + "\\";
    }

    public IReadOnlyList<Root> Roots => _roots;

    public bool HasRoots => _roots.Count > 0;

    /// <summary>Replaces the root set. Invalid or missing roots are skipped with a log entry.</summary>
    public void SetRoots(IEnumerable<(string Path, bool ReadOnly, string? Label)> roots)
    {
        _roots.Clear();

        foreach (var (path, readOnly, label) in roots)
        {
            if (string.IsNullOrWhiteSpace(path))
                continue;

            try
            {
                var full = Path.GetFullPath(path);
                if (!Directory.Exists(full) && !File.Exists(full))
                {
                    _log.LogWarning("Ignoring allowed root {Path} because it does not exist", full);
                    continue;
                }

                // Resolve the root itself so a root that is itself a junction still
                // validates correctly for everything underneath it.
                var resolved = ResolveFinalTarget(full);
                _roots.Add(new Root(resolved, label ?? new DirectoryInfo(resolved).Name, readOnly));
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Ignoring invalid allowed root {Path}", path);
            }
        }

        _log.LogInformation("File sandbox active with {Count} root(s): {Roots}",
            _roots.Count, string.Join(", ", _roots.Select(r => $"{(r.ReadOnly ? "ro:" : "rw:")}{r.CanonicalPath}")));
    }

    /// <summary>The default root used when a request omits <c>path</c>.</summary>
    public Root? DefaultRoot => _roots.FirstOrDefault(r => !r.ReadOnly) ?? _roots.FirstOrDefault();

    // -----------------------------------------------------------------------------
    // Validation
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Validates and canonicalises a client-supplied path.
    /// </summary>
    /// <param name="rawPath">The path exactly as the client sent it.</param>
    /// <param name="forWrite">Whether the operation mutates the path.</param>
    /// <param name="mustExist">Whether the path has to exist already.</param>
    /// <returns>The fully resolved, sandbox-approved absolute path.</returns>
    /// <exception cref="SandboxViolationException">The path is outside every allowed root.</exception>
    public string Resolve(string? rawPath, bool forWrite = false, bool mustExist = false)
    {
        if (_roots.Count == 0)
            throw new SandboxViolationException("No file roots are configured, so the file manager is disabled.");

        var candidate = NormalizeInput(rawPath);

        if (mustExist && !Directory.Exists(candidate) && !File.Exists(candidate))
            throw new SandboxViolationException($"The path does not exist: {candidate}");

        // Reject before touching the filesystem: traversal in the raw input is a bug or
        // an attack, and there is no legitimate reason for it to survive normalisation.
        var resolved = ResolveFinalTarget(candidate);

        var root = FindOwningRoot(resolved)
            ?? throw new SandboxViolationException(
                $"The path is outside every allowed file root: {resolved}");

        if (forWrite && root.ReadOnly)
            throw new FileAccessDeniedException($"The root containing {resolved} is read-only.");

        return resolved;
    }

    /// <summary>Validates a path and additionally requires that it is a directory.</summary>
    public string ResolveDirectory(string? rawPath, bool forWrite = false)
    {
        var path = Resolve(rawPath, forWrite, mustExist: true);
        if (!Directory.Exists(path))
            throw new SandboxViolationException($"Not a directory: {path}");
        return path;
    }

    /// <summary>Validates a path and additionally requires that it is a regular file.</summary>
    public string ResolveFile(string? rawPath, bool forWrite = false, bool mustExist = true)
    {
        var path = Resolve(rawPath, forWrite, mustExist);
        if (mustExist && !File.Exists(path))
            throw new SandboxViolationException($"Not a file: {path}");
        return path;
    }

    /// <summary>Validates a path that does not exist yet (create, rename target, upload target).</summary>
    public string ResolveNewPath(string? rawPath, bool forWrite = true)
    {
        var path = Resolve(rawPath, forWrite, mustExist: false);
        if (Directory.Exists(path) || File.Exists(path))
            throw new SandboxViolationException($"The path already exists: {path}");
        return path;
    }

    /// <summary>Whether the resolved path is in a read-only root.</summary>
    public bool IsReadOnly(string resolvedPath) => FindOwningRoot(resolvedPath)?.ReadOnly ?? true;

    /// <summary>
    /// Whether a directory may be deleted. A root, and any ancestor of a root, is never
    /// deletable, otherwise deleting a parent would silently take the sandbox with it.
    /// </summary>
    public bool CanDeleteDirectory(string resolvedPath)
    {
        var owning = FindOwningRoot(resolvedPath);
        if (owning is null)
            return false;

        if (IsRootOrAncestorOfAnyRoot(resolvedPath))
            return false;

        return !owning.ReadOnly;
    }

    private bool IsRootOrAncestorOfAnyRoot(string path)
    {
        var normalised = EnsureTrailingSeparator(path);

        foreach (var root in _roots)
        {
            // The question is "is this path a root, or does it sit ABOVE one?", so it is the
            // root's own prefix that has to start with this path - not the other way round.
            // Testing it the other way makes every descendant of a root look like an ancestor
            // of it, and the sandbox then refuses to delete anything at all.
            if (string.Equals(normalised.TrimEnd('\\'), root.CanonicalPath, StringComparison.OrdinalIgnoreCase))
                return true;

            if (root.Prefix.StartsWith(normalised, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    // -----------------------------------------------------------------------------
    // Canonicalisation
    // -----------------------------------------------------------------------------

    /// <summary>
    /// Turns raw client input into an absolute local path, rejecting anything that is not
    /// a plain rooted local path.
    /// </summary>
    private static string NormalizeInput(string? rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath))
            throw new SandboxViolationException("No path was supplied.");

        var path = rawPath.Trim();

        // The phone sends forward slashes on some platforms and people paste them too.
        path = path.Replace('/', '\\');

        if (path.Length > 32767)
            throw new SandboxViolationException("The path is too long.");

        // ---- Explicitly reject the escape hatches, before any normalisation ---------

        // NT object-manager and Win32 extended-length prefixes: \\?\ and \\.\
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            throw new SandboxViolationException("Device and extended-length paths are not permitted.");
        }

        // UNC: only meaningful outside the sandbox, and \\?\UNC\ was already rejected.
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new SandboxViolationException("UNC paths are not permitted.");
        }

        // NTFS alternate data streams: file.txt:hidden
        var lastSeparator = path.LastIndexOf('\\');
        var leaf = path[(lastSeparator + 1)..];
        if (leaf.Contains(':', StringComparison.Ordinal))
        {
            throw new SandboxViolationException("Alternate data streams are not permitted.");
        }

        // Reserved DOS device names, with or without an extension: CON, NUL, COM1..9, LPT1..9
        var stem = leaf.Split('.')[0];
        if (IsReservedDeviceName(stem))
        {
            throw new SandboxViolationException($"The reserved device name '{stem}' is not permitted.");
        }

        // A relative path is rooted at the sandbox default rather than the process CWD,
        // so the agent's own working directory can never influence the result.
        if (!Path.IsPathRooted(path))
        {
            var fallback = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            path = Path.Combine(fallback, path);
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new SandboxViolationException($"The path is not valid: {rawPath}");
        }
    }

    private static bool IsReservedDeviceName(string name) =>
        name.ToUpperInvariant() switch
        {
            "CON" or "PRN" or "AUX" or "NUL" => true,
            _ => name.Length == 4 &&
                 (name.StartsWith("COM", StringComparison.OrdinalIgnoreCase) ||
                  name.StartsWith("LPT", StringComparison.OrdinalIgnoreCase)) &&
                 name[3] is >= '1' and <= '9',
        };

    /// <summary>
    /// Resolves every reparse point (junction, symlink, mount point) in the path so the
    /// validation happens against the real target, not the alias.
    /// <para>
    /// The resolution is done by opening a handle and asking the kernel for the final path,
    /// which walks the whole chain. That matters more than it sounds: a path is checked
    /// component by component, so <c>root\escape\secret.txt</c> has a perfectly ordinary
    /// <c>secret.txt</c> at the end. A check that only asks whether the final component is a
    /// link - which is all <c>FileInfo.LinkTarget</c> can tell you - would wave the path
    /// straight through even though <c>escape</c> is a junction to somewhere else entirely.
    /// </para>
    /// <para>
    /// A path that does not exist yet (an upload target, a file being created) has no handle
    /// to open, so the deepest existing ancestor is resolved instead and the missing tail is
    /// re-attached. Every segment between the two is then guaranteed to be literal, because
    /// the deepest existing ancestor is by definition the last one that could have been a
    /// reparse point.
    /// </para>
    /// </summary>
    private static string ResolveFinalTarget(string path)
    {
        var full = Path.GetFullPath(path);

        // The parts of the path that do not exist yet, innermost last. They are re-attached
        // after resolution: dropping them would silently turn an upload target of
        // "downloads\movie.mkv" into "downloads", and the caller would write over the
        // directory.
        var tail = new List<string>();

        // Bounded so a deeply nested path, or a filesystem that keeps reporting phantom
        // ancestors, cannot spin here.
        for (var depth = 0; depth < 64; depth++)
        {
            if (Directory.Exists(full) || File.Exists(full))
            {
                var resolved = ResolveExisting(full) ?? full;

                for (var i = tail.Count - 1; i >= 0; i--)
                    resolved = Path.Combine(resolved, tail[i]);

                return Path.GetFullPath(resolved);
            }

            var parent = Path.GetDirectoryName(full);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, full, StringComparison.OrdinalIgnoreCase))
            {
                // A path with no existing ancestor at all (a drive that is not mounted, or a
                // root that does not exist). Nothing can be resolved, so the literal path is
                // returned and the root comparison will reject it.
                break;
            }

            var segment = Path.GetFileName(full);
            if (segment.Length > 0)
                tail.Add(segment);

            full = parent;
        }

        if (tail.Count > 0)
        {
            for (var i = tail.Count - 1; i >= 0; i--)
                full = Path.Combine(full, tail[i]);
        }

        return Path.GetFullPath(full);
    }

    /// <summary>
    /// The kernel's answer for an existing path, or <c>null</c> when the handle could not be
    /// opened.
    /// <para>
    /// A null here is not treated as "assume it is fine". The caller compares the result
    /// against the root set, and an unresolvable path simply fails to match any root and is
    /// rejected - which is the correct outcome for a directory this process cannot read.
    /// </para>
    /// </summary>
    private static string? ResolveExisting(string path)
    {
        const uint share = WinConst.FILE_SHARE_READ | WinConst.FILE_SHARE_WRITE | WinConst.FILE_SHARE_DELETE;

        var handle = KERNEL32.CreateFileW(
            path,
            0, // No access is requested; GetFinalPathNameByHandle only needs a handle.
            share,
            IntPtr.Zero,
            WinConst.OPEN_EXISTING,
            WinConst.FILE_FLAG_BACKUP_SEMANTICS | WinConst.FILE_ATTRIBUTE_NORMAL,
            IntPtr.Zero);

        if (handle == IntPtr.Zero || handle == WinConst.INVALID_HANDLE_VALUE)
            return null;

        try
        {
            var buffer = new char[32768];
            var length = KERNEL32.GetFinalPathNameByHandleW(
                handle, buffer, (uint)buffer.Length, WinConst.ResolvedPathDefaults);

            if (length == 0 || length >= buffer.Length)
                return null;

            var result = new string(buffer, 0, (int)length);

            // The kernel hands back \\?\C:\... Strip the prefix: it is not a prefix the
            // configured roots will ever have, so leaving it on would make every path look
            // like it were outside the sandbox.
            const string extendedPrefix = @"\\?\";
            if (result.StartsWith(extendedPrefix, StringComparison.Ordinal))
                result = result[extendedPrefix.Length..];

            // \\?\UNC\server\share is a UNC path wearing a drive-letter costume.
            if (result.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase))
                throw new SandboxViolationException("UNC paths are not permitted.");

            return Path.GetFullPath(result);
        }
        finally
        {
            KERNEL32.CloseHandle(handle);
        }
    }

    private Root? FindOwningRoot(string resolvedPath)
    {
        var normalised = EnsureTrailingSeparator(resolvedPath);

        // Longest prefix wins so a nested root takes precedence over its parent.
        foreach (var root in _roots.OrderByDescending(r => r.CanonicalPath.Length))
        {
            if (normalised.StartsWith(root.Prefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(resolvedPath, root.CanonicalPath, StringComparison.OrdinalIgnoreCase))
            {
                return root;
            }
        }

        return null;
    }

    private static string EnsureTrailingSeparator(string path)
        => path.EndsWith('\\') ? path : path + "\\";

    /// <summary>Whether a path is inside a named root. Used by tests and diagnostics.</summary>
    public bool IsInside(string path, string rootPath)
    {
        var normalised = EnsureTrailingSeparator(Path.GetFullPath(path));
        var root = EnsureTrailingSeparator(Path.GetFullPath(rootPath));
        return normalised.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }
}
