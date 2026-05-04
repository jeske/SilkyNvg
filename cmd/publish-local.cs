#!/usr/bin/env dotnet run
// publish-local.cs — Build release packages and deploy to local NuGet feed
//
// Cross-platform replacement for publish-local.ps1.
// Increments buildNumberOffset in version.jsonc using System.Text.Json,
// then packs all packable projects with clean (non-prerelease) version numbers.
// The MSBuild DeployToLocalNuGet target handles copying .nupkg to LOCAL_NUGET_REPO.
//
// Usage:
//   dotnet run cmd/publish-local.cs              # increment + build + deploy
//   dotnet run cmd/publish-local.cs --dry-run    # show what would happen, don't build

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

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

// Fallback: if version.jsonc isn't at projectRoot, use current directory
if (!File.Exists(Path.Combine(projectRoot, "version.jsonc")))
    projectRoot = Directory.GetCurrentDirectory();

string versionJsoncPath = Path.Combine(projectRoot, "version.jsonc");
string packageOutputDir = Path.Combine(projectRoot, "bin", "Packages", "Release");

// ── Require LOCAL_NUGET_REPO environment variable ───────────────────────
string? localNuGetFeedPath = Environment.GetEnvironmentVariable("LOCAL_NUGET_REPO");
if (string.IsNullOrEmpty(localNuGetFeedPath))
{
    WriteColor("ERROR: LOCAL_NUGET_REPO environment variable is not set!", ConsoleColor.Red);
    WriteColor("  Set it to your local NuGet feed directory, e.g.:", ConsoleColor.Yellow);
    WriteColor("  export LOCAL_NUGET_REPO=../LocalNuGet", ConsoleColor.Yellow);
    Environment.Exit(1);
}

// ── Read and parse version.jsonc ────────────────────────────────────────
if (!File.Exists(versionJsoncPath))
{
    WriteColor($"ERROR: version.jsonc not found at: {versionJsoncPath}", ConsoleColor.Red);
    Environment.Exit(1);
}

string versionJsoncText = File.ReadAllText(versionJsoncPath);
// Strip JSONC comments (// line comments) for System.Text.Json parsing
string jsonWithoutComments = Regex.Replace(versionJsoncText, @"//.*$", "", RegexOptions.Multiline);
var versionDoc = JsonNode.Parse(jsonWithoutComments)!;

string baseVersion = versionDoc["version"]!.GetValue<string>();
int currentBuildNumberOffset = versionDoc["buildNumberOffset"]!.GetValue<int>();
string currentVersion = $"{baseVersion}.{currentBuildNumberOffset}";

Console.WriteLine();
WriteColor("=== Publishing SilkyNvg release to local NuGet feed ===", ConsoleColor.Cyan);
WriteColor($"Current version: {currentVersion}", ConsoleColor.DarkGray);

// ── Increment buildNumberOffset in version.jsonc ────────────────────────
int newBuildNumberOffset = currentBuildNumberOffset + 1;
string newVersion = $"{baseVersion}.{newBuildNumberOffset}";

// Write back to version.jsonc, preserving the original format (comments, whitespace)
// Replace the buildNumberOffset value in the original text to preserve JSONC comments
string updatedVersionJsoncText = Regex.Replace(
    versionJsoncText,
    @"(""buildNumberOffset"":\s*)\d+",
    $"${{1}}{newBuildNumberOffset}");
File.WriteAllText(versionJsoncPath, updatedVersionJsoncText);

WriteColor($"New version:     {newVersion}", ConsoleColor.Green);
WriteColor($"Local feed:      {localNuGetFeedPath}", ConsoleColor.DarkGray);

if (dryRun)
{
    Console.WriteLine();
    WriteColor($"[DRY RUN] Would build release version {newVersion} and deploy to {localNuGetFeedPath}", ConsoleColor.Yellow);
    // Revert the increment
    File.WriteAllText(versionJsoncPath, versionJsoncText);
    WriteColor($"[DRY RUN] Reverted buildNumberOffset back to {currentBuildNumberOffset}", ConsoleColor.Yellow);
    Environment.Exit(0);
}

// ── Build and pack release packages ─────────────────────────────────────
// Ensure LOCAL_NUGET_REPO is set for child processes (MSBuild DeployToLocalNuGet target)
Environment.SetEnvironmentVariable("LOCAL_NUGET_REPO", localNuGetFeedPath);

var failedSteps = new List<string>();

// Clean old packages to avoid deploying stale versions
if (Directory.Exists(packageOutputDir))
{
    foreach (var stalePackage in Directory.GetFiles(packageOutputDir, "ArtificialNecessity.SilkyNvg*.nupkg"))
        File.Delete(stalePackage);
}

Console.WriteLine();
WriteColor("=== Building and packaging ===", ConsoleColor.Cyan);

// Pack only the 3 public packages (not the whole solution)
string[] packableProjects =
[
    Path.Combine(projectRoot, "src", "SilkyNvg.Package", "SilkyNvg.Package.csproj"),                                      // ArtificialNecessity.SilkyNvg (umbrella)
    Path.Combine(projectRoot, "src", "rendering", "SilkyNvg.Rendering.OpenGL", "SilkyNvg.Rendering.OpenGL.csproj"),        // ArtificialNecessity.SilkyNvg.Rendering.OpenGL
    Path.Combine(projectRoot, "src", "rendering", "SilkyNvg.Rendering.Veldrid", "SilkyNvg.Rendering.Veldrid.csproj"),      // ArtificialNecessity.SilkyNvg.Rendering.Veldrid
];

foreach (string projectPath in packableProjects)
{
    string projectName = Path.GetFileNameWithoutExtension(projectPath);
    WriteColor($"  Packing {projectName}...", ConsoleColor.DarkGray);

    int exitCode = RunProcess("dotnet",
        $"pack \"{projectPath}\" -c Release /p:Version={newVersion} /p:PackageVersion={newVersion} /p:NewRelease=true");

    if (exitCode != 0)
    {
        string msg = $"dotnet pack ({projectName}) failed with exit code {exitCode}";
        failedSteps.Add(msg);
        WriteColor($"ERROR: {msg}", ConsoleColor.Red);
    }
}

// ── Deploy packages to local NuGet feed ─────────────────────────────────
// The MSBuild DeployToLocalNuGet target should handle this automatically during pack.
// But verify packages were produced and report what was deployed.
if (failedSteps.Count == 0)
{
    Console.WriteLine();
    WriteColor("=== Verifying deployment to local NuGet feed ===", ConsoleColor.Cyan);

    if (!Directory.Exists(localNuGetFeedPath))
        Directory.CreateDirectory(localNuGetFeedPath);

    if (Directory.Exists(packageOutputDir))
    {
        var packageFiles = Directory.GetFiles(packageOutputDir, "ArtificialNecessity.SilkyNvg*.nupkg");
        if (packageFiles.Length > 0)
        {
            foreach (string packageFile in packageFiles)
            {
                string fileName = Path.GetFileName(packageFile);
                // Copy as fallback in case MSBuild target didn't fire
                string destPath = Path.Combine(localNuGetFeedPath, fileName);
                if (!File.Exists(destPath))
                    File.Copy(packageFile, destPath, overwrite: true);
                WriteColor($"  + {fileName}", ConsoleColor.Green);
            }
        }
        else
        {
            failedSteps.Add($"No ArtificialNecessity.SilkyNvg packages found in {packageOutputDir} after successful build");
            WriteColor("ERROR: No packages produced!", ConsoleColor.Red);
        }
    }
    else
    {
        failedSteps.Add($"Package output directory not found: {packageOutputDir}");
        WriteColor($"ERROR: Package output directory not found: {packageOutputDir}", ConsoleColor.Red);
    }
}
else
{
    Console.WriteLine();
    WriteColor("=== Skipping deploy (build failed) ===", ConsoleColor.Yellow);
}

// ── Final status banner ─────────────────────────────────────────────────
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
    WriteColor("╔══════════════════════════════════════════════════════════════╗", ConsoleColor.Green);
    WriteColor("║                   PUBLISH SUCCEEDED                         ║", ConsoleColor.Green);
    WriteColor("╚══════════════════════════════════════════════════════════════╝", ConsoleColor.Green);
    WriteColor($"  Version:  {newVersion}", ConsoleColor.Green);
    WriteColor($"  Feed:     {localNuGetFeedPath}", ConsoleColor.Green);
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

static int RunProcess(string fileName, string arguments)
{
    var psi = new ProcessStartInfo
    {
        FileName = fileName,
        Arguments = arguments,
        UseShellExecute = false,
    };

    using var process = Process.Start(psi);
    process!.WaitForExit();
    return process.ExitCode;
}
