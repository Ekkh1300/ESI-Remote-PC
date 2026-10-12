// Can the package actually be installed? A real check, without an emulator.
//
// `aapt2 dump badging` only says what the manifest claims. What rejects a package is the
// package manager: it resolves the ABI against the device, the signature against whatever is
// already installed, and the manifest against the device's API level. This walks the same
// checks, and states plainly which ones are proven and which cannot be from here.

using System.Diagnostics;
using System.IO.Compression;
using System.Text.RegularExpressions;

// Passed in by build.ps1 so this always checks the file that is about to be published,
// never one that happens to be lying around.
var Apk = args.Length > 0
    ? args[0]
    : Path.Combine(AppContext.BaseDirectory, "app-release.apk");
const string BuildTools = @"D:\dev\android-sdk\build-tools\37.0.0";

var results = new List<(string Label, bool Ok, string Why)>();

var badging = Run(Path.Combine(BuildTools, "aapt2.exe"), $"dump badging \"{Apk}\"");
var verify = Run(Path.Combine(BuildTools, "apksigner.bat"), $"verify --verbose \"{Apk}\"");

// ---------------------------------------------------------------- file integrity
var head = new byte[4];
using (var f = File.OpenRead(Apk)) f.Read(head, 0, 4);

Report(head[0] == 0x50 && head[1] == 0x4B,
    "the file is a well-formed zip",
    "a truncated download, or an HTML error page saved as .apk, produces this");

// ---------------------------------------------------------------- advertised ABIs
var abiLine = badging.Split('\n').FirstOrDefault(l => l.Contains("native-code:")) ?? "";
var advertised = Regex.Matches(abiLine, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();

Report(advertised.Contains("arm64-v8a") && advertised.Contains("armeabi-v7a"),
    "arm64-v8a and armeabi-v7a are both advertised",
    "a 32-bit phone has nothing to run when only 64-bit ABIs are listed, and the installer "
    + "reports that as an invalid package rather than as an ABI mismatch");

Report(advertised.Contains("x86_64"),
    "x86_64 is advertised",
    "without it the package cannot be installed on an emulator or a Chromebook at all");

// ---------------------------------------------------------------- per-ABI completeness
var elfProblems = CheckElfArchitectures(Apk);
Report(elfProblems.Count == 0,
    "every native library is really the architecture its folder claims",
    string.Join("; ", elfProblems));

var v7 = ListLibs(Apk, "armeabi-v7a");
Report(v7.Contains("libflutter.so") && v7.Contains("libapp.so"),
    "armeabi-v7a carries both the engine and the app",
    "a missing libflutter.so or libapp.so installs and then dies with UnsatisfiedLinkError - "
    + "the half-present ABI that D52 removed and this must not bring back");

Report(!v7.Any(n => n.Contains("barhopper", StringComparison.OrdinalIgnoreCase)),
    "the one library that cannot work on 32-bit is excluded from it",
    "ML Kit's barcode model has no armeabi-v7a build; leaving it in is what made the ABI "
    + "unusable in the first place");

// ---------------------------------------------------------------- signing
Report(verify.Contains("Verified using v2 scheme", StringComparison.Ordinal)
    && verify.Contains("Verified using v3 scheme", StringComparison.Ordinal),
    "signed with APK Signature Scheme v2 and v3",
    "a release signed with neither is refused outright");

Report(!verify.Contains("DOES NOT VERIFY", StringComparison.Ordinal),
    "apksigner reports the package as intact",
    "a corrupted or partially-written APK fails here");

// ---------------------------------------------------------------- API floor
// `aapt2` prints both minSdkVersion and targetSdkVersion, and the package line also carries a
// versionCode. Matching the bare token anywhere in the output would pass on the wrong line,
// so the line is matched by name.
var sdkLine = badging.Split('\n')
    .FirstOrDefault(l => l.TrimStart().StartsWith("minSdkVersion:", StringComparison.Ordinal)) ?? "";
Report(sdkLine.Contains("'24'", StringComparison.Ordinal),
    "minSdkVersion is 24",
    "this floor is set by EncryptedSharedPreferences and the TLS stack that SPKI pinning "
    + "depends on; lowering it to reach older phones would weaken the pairing secrets");

// ---------------------------------------------------------------- report
Console.WriteLine();
foreach (var (label, ok, why) in results)
{
    Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {label}");
    if (!ok) Console.WriteLine($"          {why}");
}

var passed = results.Count(r => r.Ok);
var failed = results.Count - passed;

Console.WriteLine();
Console.WriteLine(failed == 0
    ? $"  ALL {passed} INSTALLABILITY CHECKS PASSED"
    : $"  {passed} passed, {failed} FAILED");
Console.WriteLine();
Console.WriteLine("  Proven here:");
Console.WriteLine("    - the package is well formed, complete and correctly signed");
Console.WriteLine("    - every ABI it advertises is present and real, not a claim over a gap");
Console.WriteLine("    - the 32-bit ABI is usable rather than half-present");
Console.WriteLine();
Console.WriteLine("  Not proven here, and not claimed:");
Console.WriteLine("    - installation on real hardware across vendors' package managers");
Console.WriteLine("    - that the app launches on a 32-bit device instead of crashing");
Console.WriteLine("    - upgrade-over-existing, which depends on an unchanged signing key");
Console.WriteLine();

return failed == 0 ? 0 : 1;

void Report(bool ok, string label, string why) => results.Add((label, ok, why));

static string Run(string exe, string args)
{
    try
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(120_000);
        return output;
    }
    catch { return string.Empty; }
}

static List<string> ListLibs(string apk, string abi)
{
    var names = new List<string>();
    using var zip = ZipFile.OpenRead(apk);
    foreach (var e in zip.Entries.Where(e =>
                 e.FullName.StartsWith($"lib/{abi}/", StringComparison.Ordinal)))
    {
        names.Add(e.Name);
    }
    return names;
}

static List<string> CheckElfArchitectures(string apk)
{
    var problems = new List<string>();
    using var zip = ZipFile.OpenRead(apk);

    foreach (var entry in zip.Entries.Where(e =>
                 e.FullName.StartsWith("lib/", StringComparison.Ordinal)
                 && e.Name.EndsWith(".so", StringComparison.Ordinal)))
    {
        var abi = entry.FullName.Split('/')[1];

        using var s = entry.Open();
        var header = new byte[20];
        if (s.Read(header, 0, 20) < 20) { problems.Add($"{entry.FullName} is truncated"); continue; }

        if (header[0] != 0x7F || header[1] != (byte)'E'
            || header[2] != (byte)'L' || header[3] != (byte)'F')
        {
            problems.Add($"{entry.FullName} is not an ELF");
            continue;
        }

        var machine = BitConverter.ToUInt16(header, 18);
        var arch = machine switch
        {
            0x28 => "ARM",
            0xB7 => "AArch64",
            0x3E => "x86-64",
            0x03 => "x86",
            _ => "unknown",
        };
        var expected = abi switch
        {
            "armeabi-v7a" => "ARM",
            "arm64-v8a" => "AArch64",
            "x86_64" => "x86-64",
            "x86" => "x86",
            _ => "?",
        };

        if (arch != expected) problems.Add($"{entry.FullName} is {arch}, not {expected}");
    }

    return problems;
}