// NVG Shader Precompiler — compiles GLSL 450 shaders into precompiled bytecode for all Veldrid backends.
// Generates .generated.cs files in Shaders/Compiled/ with embedded byte arrays for Vulkan, D3D11, Metal, OpenGL, OpenGL ES.
//
// Usage: dotnet run --project EngineSrc/Arcane.Client/SilkyNvg/src/rendering/SilkyNvg.Rendering.Veldrid/Scripts/CompileShaders.csproj [-- baseDir]
// Default baseDir: EngineSrc/Arcane.Client/SilkyNvg/src/rendering/SilkyNvg.Rendering.Veldrid

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Veldrid;
using Veldrid.SPIRV;

// ================================================================
// Configuration
// ================================================================

// When run from MSBuild, the working directory is the project directory
// When run manually from project root, need to specify the backend directory
string veldridBackendDir = args.Length > 0
    ? args[0]
    : Directory.Exists("Shaders")
        ? "." // Running from project directory (MSBuild)
        : "EngineSrc/Arcane.Client/SilkyNvg/src/rendering/SilkyNvg.Rendering.Veldrid"; // Running from repo root

string shaderSourceDir = Path.Combine(veldridBackendDir, "Shaders", "Source");
string shaderCompiledDir = Path.Combine(veldridBackendDir, "Shaders", "Compiled");

Console.WriteLine("=== NVG Shader Precompiler ===");
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
// Shader pairs to compile (name → vert/frag .glsl file stems)
// ================================================================

var shaderPairs = new (string ClassName, string FileStem)[] {
    ("SolidFillShaders", "solid_fill"),
    ("TexturedShaders", "textured"),
    ("GradientShaders", "gradient"),
    ("ImagePatternShaders", "image_pattern"),
};

// ================================================================
// Cross-compilation targets and their options
// ================================================================

var crossCompileTargets = new (string BackendName, string EnumValue, CrossCompileTarget Target, CrossCompileOptions Options)[] {
    ("D3D11",    "GraphicsBackend.Direct3D11", CrossCompileTarget.HLSL, new CrossCompileOptions()),
    ("Metal",    "GraphicsBackend.Metal",      CrossCompileTarget.MSL,  new CrossCompileOptions()),
    ("OpenGL",   "GraphicsBackend.OpenGL",     CrossCompileTarget.GLSL, new CrossCompileOptions()),
    ("OpenGLES", "GraphicsBackend.OpenGLES",   CrossCompileTarget.ESSL, new CrossCompileOptions()),
};

int totalShadersCompiled = 0;

foreach (var (className, fileStem) in shaderPairs)
{
    Console.WriteLine($"\n--- Compiling: {className} ({fileStem}) ---");

    // Read GLSL source files
    string vertexGlslPath = Path.Combine(shaderSourceDir, $"{fileStem}.vert.glsl");
    string fragmentGlslPath = Path.Combine(shaderSourceDir, $"{fileStem}.frag.glsl");

    if (!File.Exists(vertexGlslPath))
    {
        Console.Error.WriteLine($"ERROR: Vertex shader not found: {vertexGlslPath}");
        Environment.Exit(1);
    }
    if (!File.Exists(fragmentGlslPath))
    {
        Console.Error.WriteLine($"ERROR: Fragment shader not found: {fragmentGlslPath}");
        Environment.Exit(1);
    }

    string vertexGlslSource = File.ReadAllText(vertexGlslPath);
    string fragmentGlslSource = File.ReadAllText(fragmentGlslPath);

    // Step 1: Compile GLSL → SPIRV
    Console.WriteLine("  Compiling GLSL → SPIRV...");
    SpirvCompilationResult vertexSpirvResult = SpirvCompilation.CompileGlslToSpirv(
        vertexGlslSource, $"{fileStem}.vert.glsl", ShaderStages.Vertex, new GlslCompileOptions(debug: false));
    SpirvCompilationResult fragmentSpirvResult = SpirvCompilation.CompileGlslToSpirv(
        fragmentGlslSource, $"{fileStem}.frag.glsl", ShaderStages.Fragment, new GlslCompileOptions(debug: false));

    byte[] vertexSpirv = vertexSpirvResult.SpirvBytes;
    byte[] fragmentSpirv = fragmentSpirvResult.SpirvBytes;
    Console.WriteLine($"    Vulkan SPIRV: vertex={vertexSpirv.Length} bytes, fragment={fragmentSpirv.Length} bytes");

    // Step 2: Cross-compile SPIRV → each backend
    var backendResults = new List<(string BackendName, string EnumValue, byte[] VertexBytes, byte[] FragmentBytes, string VertexEntry, string FragmentEntry)>();

    // Vulkan uses SPIRV directly
    backendResults.Add(("Vulkan", "GraphicsBackend.Vulkan", vertexSpirv, fragmentSpirv, "main", "main"));

    foreach (var (backendName, enumValue, crossTarget, crossOptions) in crossCompileTargets)
    {
        Console.WriteLine($"  Cross-compiling SPIRV → {backendName}...");
        VertexFragmentCompilationResult crossResult = SpirvCompilation.CompileVertexFragment(
            vertexSpirv, fragmentSpirv, crossTarget, crossOptions);

        // Entry points: Metal renames "main" to "main0", others keep "main"
        string vertexEntry = backendName == "Metal" ? "main0" : "main";
        string fragmentEntry = backendName == "Metal" ? "main0" : "main";

        // Cross-compiled shaders are text strings (HLSL/MSL/GLSL source code), convert to UTF-8 bytes
        byte[] vertexBytes = Encoding.UTF8.GetBytes(crossResult.VertexShader);
        byte[] fragmentBytes = Encoding.UTF8.GetBytes(crossResult.FragmentShader);

        Console.WriteLine($"    {backendName}: vertex={vertexBytes.Length} bytes (entry={vertexEntry}), fragment={fragmentBytes.Length} bytes (entry={fragmentEntry})");

        backendResults.Add((backendName, enumValue, vertexBytes, fragmentBytes, vertexEntry, fragmentEntry));
    }

    // Step 3: Generate C# file
    string outputPath = Path.Combine(shaderCompiledDir, $"{className}.generated.cs");
    Console.WriteLine($"  Generating: {outputPath}");

    var csharpBuilder = new StringBuilder();
    csharpBuilder.AppendLine("// ============================================================================");
    csharpBuilder.AppendLine("// AUTO-GENERATED FILE — DO NOT EDIT");
    csharpBuilder.AppendLine($"// Generated by: dotnet Scripts/CompileShaders.cs");
    csharpBuilder.AppendLine($"// Source: Shaders/Source/{fileStem}.vert.glsl + {fileStem}.frag.glsl");
    csharpBuilder.AppendLine($"// Generated: {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
    csharpBuilder.AppendLine("//");
    csharpBuilder.AppendLine("// To regenerate, run from the Veldrid backend directory:");
    csharpBuilder.AppendLine("//   dotnet Scripts/CompileShaders.cs");
    csharpBuilder.AppendLine("// ============================================================================");
    csharpBuilder.AppendLine();
    csharpBuilder.AppendLine("using System;");
    csharpBuilder.AppendLine("using Veldrid;");
    csharpBuilder.AppendLine();
    csharpBuilder.AppendLine("namespace SilkyNvg.Rendering.Veldrid");
    csharpBuilder.AppendLine("{");
    csharpBuilder.AppendLine($"    internal static class {className}");
    csharpBuilder.AppendLine("    {");

    // Generate CreateShaders method with backend dispatch
    csharpBuilder.AppendLine("        /// <summary>");
    csharpBuilder.AppendLine("        /// Creates the vertex + fragment shader pair for the current graphics backend.");
    csharpBuilder.AppendLine("        /// Uses precompiled bytecode — no runtime shader compilation needed.");
    csharpBuilder.AppendLine("        /// </summary>");
    csharpBuilder.AppendLine("        internal static Shader[] CreateShaders(ResourceFactory factory)");
    csharpBuilder.AppendLine("        {");
    csharpBuilder.AppendLine("            return factory.BackendType switch {");

    foreach (var (backendName, enumValue, vertexBytes, fragmentBytes, vertexEntry, fragmentEntry) in backendResults)
    {
        csharpBuilder.AppendLine($"                {enumValue} => PrecompiledShaderSet.CreateShaderPair(factory, {backendName}VertexBytes, \"{vertexEntry}\", {backendName}FragmentBytes, \"{fragmentEntry}\"),");
    }

    csharpBuilder.AppendLine($"                _ => throw new NotSupportedException($\"NVG {className}: unsupported graphics backend: {{factory.BackendType}}. Precompiled shaders are available for: Vulkan, D3D11, Metal, OpenGL, OpenGL ES.\")");
    csharpBuilder.AppendLine("            };");
    csharpBuilder.AppendLine("        }");
    csharpBuilder.AppendLine();

    // Generate byte arrays for each backend
    foreach (var (backendName, _, vertexBytes, fragmentBytes, _, _) in backendResults)
    {
        csharpBuilder.AppendLine($"        // === {backendName} ===");
        csharpBuilder.AppendLine();
        EmitByteArray(csharpBuilder, $"{backendName}VertexBytes", vertexBytes);
        csharpBuilder.AppendLine();
        EmitByteArray(csharpBuilder, $"{backendName}FragmentBytes", fragmentBytes);
        csharpBuilder.AppendLine();
    }

    csharpBuilder.AppendLine("    }");
    csharpBuilder.AppendLine("}");

    File.WriteAllText(outputPath, csharpBuilder.ToString());
    totalShadersCompiled++;
    Console.WriteLine($"  ✓ {className} generated successfully");
}

Console.WriteLine($"\n=== Done! Generated {totalShadersCompiled} shader files in {Path.GetFullPath(shaderCompiledDir)} ===");

// ================================================================
// Helper: emit a byte array as a C# static readonly field
// ================================================================

static void EmitByteArray(StringBuilder builder, string fieldName, byte[] byteData)
{
    builder.Append($"        private static readonly byte[] {fieldName} = new byte[] {{");

    // Format bytes in rows of 20 for readability
    const int bytesPerRow = 20;
    for (int byteIndex = 0; byteIndex < byteData.Length; byteIndex++)
    {
        if (byteIndex % bytesPerRow == 0)
        {
            builder.AppendLine();
            builder.Append("            ");
        }
        builder.Append($"0x{byteData[byteIndex]:X2}");
        if (byteIndex < byteData.Length - 1)
        {
            builder.Append(", ");
        }
    }

    builder.AppendLine();
    builder.AppendLine("        };");
}