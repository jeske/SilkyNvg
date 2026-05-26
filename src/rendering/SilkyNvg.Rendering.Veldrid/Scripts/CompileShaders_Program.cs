// NVG Shader Precompiler — compiles GLSL 450 shaders into .vdshader bundles for all Veldrid backends.
// Uses ShaderBundleCompiler from Veldrid.SPIRV to produce self-contained bundles with correct
// ResourceLayoutDescriptions for all backends (Vulkan, D3D11, Metal, OpenGL, OpenGL ES).
//
// Usage: dotnet run --project Scripts/CompileShaders.csproj [-- baseDir]
// Default baseDir: current working directory (set by MSBuild to the Veldrid backend directory)

using System;
using System.IO;
using Veldrid;
using Veldrid.SPIRV;

// ================================================================
// Configuration
// ================================================================

string veldridBackendDir = args.Length > 0
    ? args[0]
    : Directory.Exists("Shaders/Source")
        ? "." // Running from Veldrid backend directory (MSBuild default)
        : "src/rendering/SilkyNvg.Rendering.Veldrid"; // Running from SilkyNvg root

string shaderSourceDir = Path.Combine(veldridBackendDir, "Shaders", "Source");
string shaderCompiledDir = Path.Combine(veldridBackendDir, "Shaders", "Compiled");

Console.WriteLine("=== NVG Shader Precompiler (.vdshader bundles) ===");
Console.WriteLine($"  Source dir:   {Path.GetFullPath(shaderSourceDir)}");
Console.WriteLine($"  Output dir:   {Path.GetFullPath(shaderCompiledDir)}");

if (!Directory.Exists(shaderSourceDir))
{
    Console.Error.WriteLine($"ERROR: Shader source directory not found: {shaderSourceDir}");
    Console.Error.WriteLine("Run this script from the project root, or pass the Veldrid backend directory as an argument.");
    Environment.Exit(1);
}

Directory.CreateDirectory(shaderCompiledDir);

// ================================================================
// Shader pairs to compile (bundleName → vert/frag .glsl file stems)
// ================================================================

var shaderPairs = new (string BundleName, string FileStem)[] {
    ("SolidFill", "solid_fill"),
    ("Textured", "textured"),
    ("Gradient", "gradient"),
    ("ImagePattern", "image_pattern"),
};

// ================================================================
// Incremental compilation: check if recompilation is needed
// ================================================================

bool needsRecompilation = false;
DateTime newestInputTimestamp = DateTime.MinValue;
DateTime oldestOutputTimestamp = DateTime.MaxValue;

foreach (var (_, fileStem) in shaderPairs) {
    string vertexGlslPath = Path.Combine(shaderSourceDir, $"{fileStem}.vert.glsl");
    string fragmentGlslPath = Path.Combine(shaderSourceDir, $"{fileStem}.frag.glsl");
    
    if (File.Exists(vertexGlslPath)) {
        DateTime ts = File.GetLastWriteTimeUtc(vertexGlslPath);
        if (ts > newestInputTimestamp) newestInputTimestamp = ts;
    }
    if (File.Exists(fragmentGlslPath)) {
        DateTime ts = File.GetLastWriteTimeUtc(fragmentGlslPath);
        if (ts > newestInputTimestamp) newestInputTimestamp = ts;
    }
}

foreach (var (bundleName, _) in shaderPairs) {
    string outputPath = Path.Combine(shaderCompiledDir, $"{bundleName}.vdshader");
    if (!File.Exists(outputPath)) {
        needsRecompilation = true;
        Console.WriteLine($"  Output file missing: {bundleName}.vdshader");
        break;
    }
    DateTime ts = File.GetLastWriteTimeUtc(outputPath);
    if (ts < oldestOutputTimestamp) oldestOutputTimestamp = ts;
}

if (!needsRecompilation && newestInputTimestamp > oldestOutputTimestamp) {
    needsRecompilation = true;
    Console.WriteLine($"  Input files modified more recently than outputs");
}

if (!needsRecompilation) {
    Console.WriteLine("  All shader outputs are up-to-date, skipping compilation");
    Console.WriteLine($"  Newest input:  {newestInputTimestamp:yyyy-MM-dd HH:mm:ss} UTC");
    Console.WriteLine($"  Oldest output: {oldestOutputTimestamp:yyyy-MM-dd HH:mm:ss} UTC");
    Console.WriteLine("\n=== Shader compilation skipped (outputs up-to-date) ===");
    return;
}

Console.WriteLine("  Recompilation needed:");
Console.WriteLine($"    Newest input:  {newestInputTimestamp:yyyy-MM-dd HH:mm:ss} UTC");
Console.WriteLine($"    Oldest output: {oldestOutputTimestamp:yyyy-MM-dd HH:mm:ss} UTC");

// ================================================================
// Compile each shader pair using ShaderBundleCompiler
// ================================================================

int totalBundlesCompiled = 0;

foreach (var (bundleName, fileStem) in shaderPairs)
{
    Console.WriteLine($"\n--- Compiling: {bundleName} ({fileStem}) ---");

    string vertexGlslPath = Path.Combine(shaderSourceDir, $"{fileStem}.vert.glsl");
    string fragmentGlslPath = Path.Combine(shaderSourceDir, $"{fileStem}.frag.glsl");

    if (!File.Exists(vertexGlslPath)) {
        Console.Error.WriteLine($"ERROR: Vertex shader not found: {vertexGlslPath}");
        Environment.Exit(1);
    }
    if (!File.Exists(fragmentGlslPath)) {
        Console.Error.WriteLine($"ERROR: Fragment shader not found: {fragmentGlslPath}");
        Environment.Exit(1);
    }

    string vertexGlsl = File.ReadAllText(vertexGlslPath);
    string fragmentGlsl = File.ReadAllText(fragmentGlslPath);

    // ShaderBundleCompiler handles everything: GLSL→SPIRV, cross-compilation to all backends,
    // reflection capture, hashing, and bundle assembly.
    VeldridShaderBundle bundle = ShaderBundleCompiler.CompileVertexFragment(
        vertexGlsl,
        fragmentGlsl,
        bundleName,
        vertexSourceFile: $"{fileStem}.vert.glsl",
        fragmentSourceFile: $"{fileStem}.frag.glsl");

    // Write the .vdshader bundle
    string bundlePath = Path.Combine(shaderCompiledDir, $"{bundleName}.vdshader");
    bundle.SerializeToFile(bundlePath);
    Console.WriteLine($"  ✓ {bundleName}.vdshader ({new FileInfo(bundlePath).Length} bytes)");
    Console.WriteLine($"    ResourceLayouts: {bundle.ResourceLayoutDescriptions?.Length ?? 0} sets");
    Console.WriteLine($"    Backends: {string.Join(", ", bundle.Backends.Keys)}");
    totalBundlesCompiled++;
}

Console.WriteLine($"\n=== Done! Generated {totalBundlesCompiled} .vdshader bundles in {Path.GetFullPath(shaderCompiledDir)} ===");
