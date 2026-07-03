#!/usr/bin/env -S dotnet run
// publish-local.cs — Cross-platform build+pack+deploy to local NuGet feed
//
// Versioning is timestamp-based (v2) — every build gets a unique version
// automatically via Silky.shared.Build.props. No version files to manage.
// The timestamp is captured once here and passed to MSBuild so all projects
// in the solution get the exact same version (no inter-project skew).
//
// Requires: LOCAL_NUGET_REPO environment variable must be set
//
// Usage:
//   dotnet run cmd/publish-local.cs              # build + pack + deploy
//   dotnet run cmd/publish-local.cs --dry-run    # show what would happen, don't build

using System.Diagnostics;

// ── Disable MSBuild node reuse to prevent zombie worker nodes ────────────
// MSBuild nodes with --nodeReuse:true stay alive between builds to "speed up"
// subsequent builds. On Linux, if the parent dies (Ctrl+C), these nodes become
// immortal zombies that hold locks and corrupt future restore/build operations.
Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", "1");

// ── Ctrl+C handling — kill child processes to prevent zombies ────────────
// .NET's Process.Start on Linux uses posix_spawn, which does NOT place the
// child in the parent's process group. Ctrl+C (SIGINT) only reaches the
// foreground pgroup, leaving children alive as zombies. We track the active
// child and kill its entire process tree on cancellation.
Process? _activeChild = null;
var _cts = new CancellationTokenSource();

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true; // prevent immediate exit so we can clean up
    _cts.Cancel();
    var proc = _activeChild;
    if (proc is { HasExited: false })
    {
        WriteColor($"\n[Ctrl+C] Killing child process tree (PID {proc.Id})...", ConsoleColor.Yellow);
        try
        {
            // Kill the entire process tree (-pid sends to process group if we can,
            // otherwise kill the process directly and MSBuild nodes via build-server)
            proc.Kill(entireProcessTree: true);
        }
        catch { /* best effort */ }
    }
    // Also shut down any MSBuild nodes that may have been spawned with node reuse
    try
    {
        using var shutdown = Process.Start(new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = "build-server shutdown",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        shutdown?.WaitForExit(5000);
    }
    catch { /* best effort */ }

    Environment.Exit(130); // Standard Ctrl+C exit code
};

// ── Parse arguments ─────────────────────────────────────────────────────
bool dryRun = args.Any(a => a is "--dry-run" or "-n");

// ── Resolve project root (the script lives in cmd/) ─────────────────────
// When invoked via `dotnet run cmd/publish-local.cs` from the repo root,
// the working directory is the repo root. But handle running from cmd/ too.
string scriptDir = Path.GetDirectoryName(Path.GetFullPath(
    AppContext.BaseDirectory.Contains("publish-local")
        ? Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "cmd", "publish-local.cs")
        : "cmd/publish-local.cs"))!;
string projectRoot = Path.GetFullPath(Path.Combine(scriptDir, ".."));

// Fallback: if SilkyNvg.sln isn't at projectRoot, use current directory
if (!File.Exists(Path.Combine(projectRoot, "SilkyNvg.sln")))
    projectRoot = Directory.GetCurrentDirectory();

string packageOutputDir = Path.Combine(projectRoot, "bin", "Packages", "Release");

// ── Require git submodule (SafeStbTrueTypeSharp) ────────────────────────────
// The build depends on SafeStbTrueTypeSharp which lives in a git submodule.
// After a fresh clone the submodule directory is empty until initialized.
string submoduleSln = Path.Combine(projectRoot, "SafeStbTrueTypeSharp", "SafeStbTrueTypeSharp.sln");
if (!File.Exists(submoduleSln))
{
    WriteColor("ERROR: Git submodule 'SafeStbTrueTypeSharp' is not initialized!", ConsoleColor.Red);
    WriteColor("  The SafeStbTrueTypeSharp/ directory is empty or missing.", ConsoleColor.Yellow);
    WriteColor("  Run the following from the repository root:", ConsoleColor.Yellow);
    WriteColor("    git submodule init && git submodule update", ConsoleColor.Cyan);
    Environment.Exit(1);
}

// ── Require LOCAL_NUGET_REPO environment variable ───────────────────────────
string? localNuGetFeedPath = Environment.GetEnvironmentVariable("LOCAL_NUGET_REPO");
if (string.IsNullOrEmpty(localNuGetFeedPath))
{
    WriteColor("ERROR: LOCAL_NUGET_REPO environment variable is not set!", ConsoleColor.Red);
    WriteColor("  Set it to your local NuGet feed directory, e.g.:", ConsoleColor.Yellow);
    WriteColor("  export LOCAL_NUGET_REPO=../LocalNuGet", ConsoleColor.Yellow);
    Environment.Exit(1);
}

// ── Capture timestamp ONCE so all projects get the same version ─────────
var now = DateTime.Now;
string buildYYMM   = now.ToString("yyMM");
string buildDDHH   = now.ToString("ddHH");
string buildmmss   = now.ToString("mmss");
string buildYYMMDD = now.ToString("yyMMdd");
string buildHHmmss = now.ToString("HHmmss");
string[] versionProps = [$"/p:_BuildYYMM={buildYYMM}", $"/p:_BuildDDHH={buildDDHH}", $"/p:_Buildmmss={buildmmss}", $"/p:_BuildYYMMDD={buildYYMMDD}", $"/p:_BuildHHmmss={buildHHmmss}"];
// NuGet normalizes version numbers by stripping leading zeros from numeric segments
string packageVersion = $"1.{int.Parse(buildYYMMDD)}.{int.Parse(buildHHmmss)}";

Console.WriteLine();
WriteColor("=== Publishing SilkyNvg release to local NuGet feed ===", ConsoleColor.Cyan);
WriteColor($"Version stamp: 1.{buildYYMM}.{buildDDHH}.{buildmmss} (pkg: {packageVersion})", ConsoleColor.DarkGray);
WriteColor($"Local feed:    {localNuGetFeedPath}", ConsoleColor.DarkGray);

if (dryRun)
{
    Console.WriteLine();
    WriteColor($"[DRY RUN] Would build and deploy version {packageVersion} to {localNuGetFeedPath}", ConsoleColor.Yellow);
    Environment.Exit(0);
}

// ── Build and pack release packages ─────────────────────────────────────
Environment.SetEnvironmentVariable("LOCAL_NUGET_REPO", localNuGetFeedPath);
var failedSteps = new List<string>();

// Clean old packages to avoid deploying stale versions
if (Directory.Exists(packageOutputDir))
{
    foreach (var stalePackage in Directory.GetFiles(packageOutputDir, "ArtificialNecessity.SilkyNvg*.nupkg"))
        File.Delete(stalePackage);
}

// Capture timestamp before build/pack so we can identify newly deployed packages
var deployStartTime = DateTime.Now;

Console.WriteLine();
WriteColor("=== Building and packaging ===", ConsoleColor.Cyan);

// Pack only the 3 public packages (not the whole solution, which would produce unwanted granular packages)
string[] packableProjects =
[
    Path.Combine(projectRoot, "src", "SilkyNvg.Package", "SilkyNvg.Package.csproj"),                                      // ArtificialNecessity.SilkyNvg (umbrella)
    Path.Combine(projectRoot, "src", "rendering", "SilkyNvg.Rendering.OpenGL", "SilkyNvg.Rendering.OpenGL.csproj"),        // ArtificialNecessity.SilkyNvg.Rendering.OpenGL
    Path.Combine(projectRoot, "src", "rendering", "SilkyNvg.Rendering.Veldrid", "SilkyNvg.Rendering.Veldrid.csproj"),      // ArtificialNecessity.SilkyNvg.Rendering.Veldrid
];

foreach (string projectPath in packableProjects)
{
    if (_cts.IsCancellationRequested) break;

    string projectName = Path.GetFileNameWithoutExtension(projectPath);
    WriteColor($"  Packing {projectName}...", ConsoleColor.DarkGray);

    string versionArgs = string.Join(" ", versionProps);
    int exitCode = RunProcess("dotnet", $"pack \"{projectPath}\" -c Release /nodeReuse:false {versionArgs}");

    if (exitCode != 0)
    {
        string msg = $"dotnet pack ({projectName}) failed with exit code {exitCode}";
        failedSteps.Add(msg);
        WriteColor($"ERROR: {msg}", ConsoleColor.Red);
    }
}

// ── Final status ────────────────────────────────────────────────────────
Console.WriteLine();
if (failedSteps.Count > 0)
{
    WriteColor("╔══════════════════════════════════════════════════════════════╗", ConsoleColor.Red);
    WriteColor("║                    PUBLISH FAILED                           ║", ConsoleColor.Red);
    WriteColor("╚══════════════════════════════════════════════════════════════╝", ConsoleColor.Red);
    foreach (string step in failedSteps)
        WriteColor($"  ✗ {step}", ConsoleColor.Red);
    Console.WriteLine();
    Environment.Exit(1);
}
else
{
    // Show packages deployed during this run
    var deployedPackages = Directory.Exists(localNuGetFeedPath)
        ? Directory.GetFiles(localNuGetFeedPath, "*.nupkg")
            .Select(f => new FileInfo(f))
            .Where(fi => fi.LastWriteTime >= deployStartTime)
            .OrderBy(fi => fi.Name)
            .ToArray()
        : [];

    WriteColor("╔══════════════════════════════════════════════════════════════╗", ConsoleColor.Green);
    WriteColor("║                   PUBLISH SUCCEEDED                         ║", ConsoleColor.Green);
    WriteColor("╚══════════════════════════════════════════════════════════════╝", ConsoleColor.Green);

    if (deployedPackages.Length > 0)
    {
        Console.WriteLine();
        WriteColor("Deployed packages:", ConsoleColor.Cyan);
        foreach (var pkg in deployedPackages)
        {
            double sizeKB = Math.Round(pkg.Length / 1024.0, 1);
            WriteColor($"  {pkg.Name}  ({sizeKB} KB)", ConsoleColor.Green);
        }
    }
    else
    {
        WriteColor($"  Version:  {packageVersion}", ConsoleColor.Green);
        WriteColor($"  Feed:     {localNuGetFeedPath}", ConsoleColor.Green);
    }
    Console.WriteLine();
}

// ── Helper functions ────────────────────────────────────────────────────

static void WriteColor(string message, ConsoleColor color)
{
    var prev = Console.ForegroundColor;
    Console.ForegroundColor = color;
    Console.WriteLine(message);
    Console.ForegroundColor = prev;
}

int RunProcess(string fileName, string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
    };

    using var process = Process.Start(psi);
    _activeChild = process;
    try
    {
        process!.WaitForExit();
    }
    finally
    {
        _activeChild = null;
    }
    return _cts.IsCancellationRequested ? -1 : process!.ExitCode;
}
