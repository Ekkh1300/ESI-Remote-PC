using Esi.Platform.Files;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Esi.Tests;

/// <summary>
/// The filesystem security boundary.
/// <para>
/// Every one of these tests is an attack. The sandbox is the only thing standing between an
/// HTTP request on the LAN and <c>C:\Windows\System32</c>, so a case that "probably cannot
/// happen" is a case that will happen.
/// </para>
/// <para>
/// The roots are real temporary directories on a real NTFS volume rather than mocks: the
/// interesting behaviours here (8.3 names, junctions, alternate data streams, short-name
/// aliasing) are properties of the filesystem, not of the code, and a mock would only ever
/// prove the code agrees with the mock.
/// </para>
/// </summary>
[Collection(nameof(FileSandboxTests))]
public sealed class FileSandboxTests : IDisposable
{
    private readonly string _root;
    private readonly string _outside;
    private readonly string _readOnlyRoot;
    private readonly FileSandbox _sandbox;

    public FileSandboxTests()
    {
        // A short path: the 8.3 name of a deeply nested temp directory can be longer than
        // the short name itself, which would make the short-name test vacuous.
        var baseDirectory = Path.Combine(Path.GetTempPath(), "esisb" + Guid.NewGuid().ToString("n").Substring(0, 8));
        _root = Path.Combine(baseDirectory, "rw");
        _readOnlyRoot = Path.Combine(baseDirectory, "ro");
        _outside = Path.Combine(baseDirectory, "outside");

        Directory.CreateDirectory(_root);
        Directory.CreateDirectory(_readOnlyRoot);
        Directory.CreateDirectory(_outside);
        Directory.CreateDirectory(Path.Combine(_root, "nested", "deeper"));

        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello");
        File.WriteAllText(Path.Combine(_outside, "secret.txt"), "top secret");

        _sandbox = new FileSandbox(NullLogger<FileSandbox>.Instance);
        _sandbox.SetRoots(new (string Path, bool ReadOnly, string? Label)[]
        {
            (_root, false, "rw"),
            (_readOnlyRoot, true, "ro"),
        });
    }

    public void Dispose()
    {
        try
        {
            var baseDirectory = Path.GetDirectoryName(_root)!;
            if (Directory.Exists(baseDirectory))
                Directory.Delete(baseDirectory, recursive: true);
        }
        catch (IOException)
        {
            // A handle held by the OS after a test is not a test failure.
        }
    }

    // ---------------------------------------------------------------------------
    // The happy path
    // ---------------------------------------------------------------------------

    [Fact]
    public void A_path_inside_a_writable_root_resolves()
    {
        var resolved = _sandbox.Resolve(Path.Combine(_root, "hello.txt"));

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "hello.txt")), resolved);
    }

    [Fact]
    public void ANested_path_inside_a_root_resolves()
    {
        var resolved = _sandbox.Resolve(Path.Combine(_root, "nested", "deeper"));

        Assert.StartsWith(_root, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_forward_slash_path_is_accepted()
    {
        // The phone sends forward slashes on some platforms, and people paste them.
        var resolved = _sandbox.Resolve(_root.Replace('\\', '/') + "/hello.txt");

        Assert.Equal(Path.GetFullPath(Path.Combine(_root, "hello.txt")), resolved);
    }

    [Fact]
    public void Roots_are_reported_with_their_access()
    {
        Assert.Equal(2, _sandbox.Roots.Count);
        Assert.Single(_sandbox.Roots, r => r.ReadOnly);
        Assert.Single(_sandbox.Roots, r => !r.ReadOnly);
        Assert.False(_sandbox.IsReadOnly(Path.Combine(_root, "hello.txt")));
        Assert.True(_sandbox.IsReadOnly(Path.Combine(_readOnlyRoot, "anything.txt")));
    }

    [Fact]
    public void WithNoRoots_everything_is_denied()
    {
        // Fail closed: a misconfigured agent must expose no files at all, not all of them.
        var empty = new FileSandbox(NullLogger<FileSandbox>.Instance);

        Assert.False(empty.HasRoots);
        Assert.Throws<SandboxViolationException>(() => empty.Resolve(_root));
    }

    // ---------------------------------------------------------------------------
    // Traversal
    // ---------------------------------------------------------------------------

    [Fact]
    public void Parent_traversal_that_escapes_the_root_is_rejected()
    {
        var escape = Path.Combine(_root, "..", "outside", "secret.txt");

        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(escape));
    }

    [Fact]
    public void Deep_parent_traversal_is_rejected()
    {
        var escape = Path.Combine(_root, "nested", "..", "..", "..", "..", "Windows", "System32", "config", "SAM");

        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(escape));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../")]
    [InlineData(@"..\")]
    [InlineData("..\\..")]
    [InlineData("nested/../../outside/secret.txt")]
    public void Traversal_forms_are_all_rejected(string suffix)
    {
        var escape = _root + "\\" + suffix.Replace('/', '\\');

        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(escape));
    }

    [Fact]
    public void Traversal_that_stays_inside_the_root_is_allowed()
    {
        // ..\nested\deeper never leaves the sandbox, so rejecting it would break ordinary
        // use of the file manager for no security gain.
        var inside = Path.Combine(_root, "nested", "..", "nested", "deeper");

        var resolved = _sandbox.Resolve(inside);

        Assert.StartsWith(_root, resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_absolute_path_to_a_sensitive_location_is_rejected()
    {
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(@"C:\Windows\System32\config\SAM"));
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(@"C:\Program Files"));
    }

    [Fact]
    public void A_sibling_directory_with_a_shared_name_prefix_is_rejected()
    {
        // The classic off-by-one prefix bug: C:\Temp\root-evil starts with C:\Temp\root
        // as a *string* but is a different directory. The comparison must be on a
        // separator-terminated prefix, not a raw StartsWith.
        var sibling = _root + "-evil";
        Directory.CreateDirectory(sibling);

        try
        {
            Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(Path.Combine(sibling, "x.txt")));
        }
        finally
        {
            Directory.Delete(sibling, recursive: true);
        }
    }

    [Fact]
    public void A_null_or_empty_path_is_rejected()
    {
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(null));
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(""));
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve("   "));
    }

    // ---------------------------------------------------------------------------
    // The escape hatches, each rejected before any filesystem call
    // ---------------------------------------------------------------------------

    [Fact]
    public void Device_and_extended_length_paths_are_rejected()
    {
        // \\?\ bypasses Win32 path normalisation entirely, so a naive canonicalise-then-
        // compare check would accept whatever it points at.
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(@"\\?\C:\Windows\System32"));
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(@"\\.\PhysicalDrive0"));
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(@"\\?\UNC\server\share\file"));
    }

    [Fact]
    public void Unc_paths_are_rejected()
    {
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(@"\\server\share\file.txt"));
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(@"\\localhost\c$\Windows"));
    }

    [Fact]
    public void Alternate_data_streams_are_rejected()
    {
        // file.txt:hidden is a completely separate stream that is invisible to a directory
        // listing, so it is both a hiding place and a way to write outside a read-only root.
        Assert.Throws<SandboxViolationException>(
            () => _sandbox.Resolve(Path.Combine(_root, "hello.txt") + ":hidden"));

        Assert.Throws<SandboxViolationException>(
            () => _sandbox.Resolve(Path.Combine(_root, "hello.txt") + ":$DATA"));
    }

    [Theory]
    [InlineData("CON")]
    [InlineData("NUL")]
    [InlineData("PRN")]
    [InlineData("AUX")]
    [InlineData("COM1")]
    [InlineData("COM9")]
    [InlineData("LPT1")]
    [InlineData("LPT9")]
    [InlineData("con")]
    [InlineData("nul")]
    public void Reserved_dos_device_names_are_rejected(string name)
    {
        // \\.\CON, and any path whose leaf is CON, reaches a device rather than a file.
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(Path.Combine(_root, name)));
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(Path.Combine(_root, name + ".txt")));
    }

    [Fact]
    public void An_absurdly_long_path_is_rejected_rather_than_throwing()
    {
        var longPath = _root + "\\" + new string('a', 40000);

        // Must be a SandboxViolationException, not a PathTooLongException escaping the
        // boundary: the endpoint maps the former to a 403 and the latter to a 500.
        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(longPath));
    }

    // ---------------------------------------------------------------------------
    // NTFS 8.3 short names
    // ---------------------------------------------------------------------------

    [Fact]
    public void An_8_3_short_name_resolves_to_its_real_path()
    {
        var longName = "a-folder-name-long-enough-to-be-shortened";
        var longPath = Path.Combine(_root, longName);
        Directory.CreateDirectory(longPath);

        // Ask Windows for the short name. This volume may have 8.3 generation disabled, in
        // which case GetShortPathName just hands back the long name and there is no alias to
        // test - that is a legitimate configuration, not a failure.
        var shortName = GetShortName(longPath);
        if (shortName is null || shortName.Equals(longName, StringComparison.OrdinalIgnoreCase))
            return;

        var resolved = _sandbox.Resolve(Path.Combine(_root, shortName, "file.txt"));

        // The resolved path must be the long one: a short name is an alias, and letting the
        // alias through unchanged would let the prefix comparison run against a form the
        // caller controls.
        Assert.DoesNotContain(shortName, resolved, StringComparison.OrdinalIgnoreCase);
        Assert.StartsWith(longPath, resolved, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------------------
    // Junctions and symlinks
    // ---------------------------------------------------------------------------

    [Fact]
    public void A_junction_pointing_outside_the_root_is_rejected()
    {
        var junction = Path.Combine(_root, "escape");

        if (!TryCreateJunction(junction, _outside))
            return; // Creating one needs Developer Mode or elevation; nothing to assert.

        try
        {
            // The junction itself is inside the root, so a prefix check alone would pass it.
            // Only resolving the final target catches this.
            var viaJunction = Path.Combine(junction, "secret.txt");

            var ex = Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(viaJunction));
            Assert.Contains("outside every allowed file root", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            DeleteJunction(junction);
        }
    }

    [Fact]
    public void A_symlink_pointing_outside_the_root_is_rejected()
    {
        var link = Path.Combine(_root, "escape-link");
        var target = Path.Combine(_outside, "secret.txt");

        if (!TryCreateFileSymlink(link, target))
            return;

        try
        {
            Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(link));
        }
        finally
        {
            File.Delete(link);
        }
    }

    [Fact]
    public void A_junction_that_stays_inside_the_root_is_allowed()
    {
        var junction = Path.Combine(_root, "inside");
        var target = Path.Combine(_root, "nested");

        if (!TryCreateJunction(junction, target))
            return;

        try
        {
            // Resolving through a legitimate alias must keep working; otherwise the sandbox
            // would break any user who has reorganised their folders with junctions.
            var resolved = _sandbox.Resolve(Path.Combine(junction, "deeper"));

            Assert.StartsWith(_root, resolved, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteJunction(junction);
        }
    }

    // ---------------------------------------------------------------------------
    // Read-only roots
    // ---------------------------------------------------------------------------

    [Fact]
    public void Writing_into_a_read_only_root_is_denied()
    {
        var target = Path.Combine(_readOnlyRoot, "new.txt");

        var ex = Assert.Throws<FileAccessDeniedException>(() => _sandbox.Resolve(target, forWrite: true));
        Assert.Equal("esi_forbidden_scope", ex.Code);
    }

    [Fact]
    public void Reading_from_a_read_only_root_is_allowed()
    {
        var target = Path.Combine(_readOnlyRoot, "readable.txt");
        File.WriteAllText(target, "read me");

        _sandbox.Resolve(target, forWrite: false);
    }

    [Fact]
    public void A_root_itself_cannot_be_deleted()
    {
        // Otherwise a single delete request takes the whole sandbox - and, since the parent
        // is what the user configured, potentially much more.
        Assert.False(_sandbox.CanDeleteDirectory(_sandbox.Resolve(_root)));
    }

    [Fact]
    public void A_subdirectory_of_a_root_can_be_deleted_when_the_root_is_writable()
    {
        var child = Path.Combine(_root, "nested", "deeper");
        var resolved = _sandbox.Resolve(child);

        Assert.True(_sandbox.CanDeleteDirectory(resolved));
    }

    [Fact]
    public void A_directory_in_a_read_only_root_cannot_be_deleted()
    {
        var child = Path.Combine(_readOnlyRoot, "sub");
        Directory.CreateDirectory(child);

        Assert.False(_sandbox.CanDeleteDirectory(_sandbox.Resolve(child)));
    }

    // ---------------------------------------------------------------------------
    // Typed resolution
    // ---------------------------------------------------------------------------

    [Fact]
    public void ResolveDirectory_rejects_a_file()
    {
        var ex = Assert.Throws<SandboxViolationException>(
            () => _sandbox.ResolveDirectory(Path.Combine(_root, "hello.txt")));

        Assert.Contains("Not a directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveFile_rejects_a_directory()
    {
        var ex = Assert.Throws<SandboxViolationException>(
            () => _sandbox.ResolveFile(Path.Combine(_root, "nested")));

        Assert.Contains("Not a file", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveNewPath_rejects_a_path_that_already_exists()
    {
        // Creating a file over an existing one, or creating a directory where a file is,
        // is a way to change the type of a path the user believes they already control.
        var ex = Assert.Throws<SandboxViolationException>(
            () => _sandbox.ResolveNewPath(Path.Combine(_root, "hello.txt")));

        Assert.Contains("already exists", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveNewPath_accepts_a_path_that_does_not_exist_yet()
    {
        var target = Path.Combine(_root, "brand-new.txt");

        Assert.Equal(Path.GetFullPath(target), _sandbox.ResolveNewPath(target));
    }

    [Fact]
    public void Resolve_with_mustExist_rejects_a_missing_path()
    {
        var missing = Path.Combine(_root, "not-here.txt");

        Assert.Throws<SandboxViolationException>(() => _sandbox.Resolve(missing, mustExist: true));
        _sandbox.Resolve(missing, mustExist: false);
    }

    // ---------------------------------------------------------------------------
    // Helpers that need Windows APIs the test itself has to set up
    // ---------------------------------------------------------------------------

    private static string? GetShortName(string longPath)
    {
        const uint shortNameBuffer = 0x1000;

        // A StringBuilder is the only shape GetShortPathNameW will marshal. ToString() rather
        // than ToString(0, n) because the out-marshaller sets Length from the return value
        // including the terminator, so the count is not a safe substring bound.
        var buffer = new System.Text.StringBuilder((int)shortNameBuffer);
        var length = (uint)shortNameBuffer;

        var ok = Kernel32.GetShortPathName(longPath, buffer, ref length);
        if (ok == 0 || ok >= shortNameBuffer)
            return null; // Not available: 8.3 generation is off on this volume.

        var result = buffer.ToString().TrimEnd('\0');
        return result.Length == 0 ? null : result;
    }

    private static bool TryCreateJunction(string junction, string target)
    {
        try
        {
            // mklink /J needs no elevation, unlike a directory symlink.
            var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe")
            {
                Arguments = $"/c mklink /J \"{junction}\" \"{target}\"",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });

            if (process is null)
                return false;

            process.WaitForExit(10_000);
            return process.ExitCode == 0 && Directory.Exists(junction);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void DeleteJunction(string junction)
    {
        try
        {
            // Deleting the reparse point itself, never its target.
            Directory.Delete(junction, recursive: false);
        }
        catch (IOException)
        {
            // Already gone, or the test volume does not support junctions.
        }
    }

    private static bool TryCreateFileSymlink(string link, string target)
    {
        try
        {
            File.CreateSymbolicLink(link, target);
            return true;
        }
        catch (Exception)
        {
            // Creating a symlink needs Developer Mode or elevation.
            return false;
        }
    }

    private static class Kernel32
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll",
            CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
        internal static extern uint GetShortPathName(string lpszLongPath,
            System.Text.StringBuilder lpszShortPath, ref uint cchBuffer);
    }
}
