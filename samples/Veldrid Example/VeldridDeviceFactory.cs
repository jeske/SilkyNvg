using System;
using System.IO;
using System.Runtime.InteropServices;
using Silk.NET.Windowing;
using Silk.NET.Maths;
using Veldrid;
using Veldrid.OpenGL;

/// <summary>
/// Platform detection, backend discovery, and GraphicsDevice creation.
/// Encapsulates all platform-specific windowing/device logic so Program.cs stays clean.
/// </summary>
static class VeldridDeviceFactory
{
    /// <summary>
    /// Backend preference order: D3D11 > Metal > Vulkan > OpenGL > OpenGLES.
    /// The first available backend on the current platform wins.
    /// </summary>
    private static readonly GraphicsBackend[] PreferenceOrder =
    [
        GraphicsBackend.Direct3D11,
        GraphicsBackend.Metal,
        GraphicsBackend.Vulkan,
        GraphicsBackend.OpenGL,
        GraphicsBackend.OpenGLES,
    ];

    /// <summary>Returns a human-readable platform name (e.g., "macOS (arm64)").</summary>
    public static string GetPlatformName()
    {
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "macOS"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux"
            : "Unknown";
        return $"{os} ({RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()})";
    }

    /// <summary>Returns all Veldrid backends available on the current platform.</summary>
    public static GraphicsBackend[] GetAvailableBackends()
    {
        // On macOS, the Vulkan loader needs VK_ICD_FILENAMES to find MoltenVK.
        // The Vulkan SDK doesn't install system-wide; set this if the SDK is present.
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("VK_ICD_FILENAMES")))
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            // Check common Vulkan SDK install locations
            string[] icdSearchPaths =
            [
                Path.Combine(home, "VulkanSDK"),  // ~/VulkanSDK/{version}/macOS/share/vulkan/icd.d/
            ];
            foreach (string sdkRoot in icdSearchPaths)
            {
                if (Directory.Exists(sdkRoot))
                {
                    // Find the latest version's MoltenVK ICD JSON
                    var icdFiles = Directory.GetFiles(sdkRoot, "MoltenVK_icd.json", SearchOption.AllDirectories);
                    var macosIcd = Array.Find(icdFiles, f => f.Contains("macOS"));
                    if (macosIcd != null)
                    {
                        Environment.SetEnvironmentVariable("VK_ICD_FILENAMES", macosIcd);
                        Environment.SetEnvironmentVariable("VK_DRIVER_FILES", macosIcd);
                        break;
                    }
                }
            }
        }

        var available = new System.Collections.Generic.List<GraphicsBackend>();
        foreach (var backend in PreferenceOrder)
        {
            if (GraphicsDevice.IsBackendSupported(backend))
                available.Add(backend);
        }
        return available.ToArray();
    }

    /// <summary>
    /// Selects the best backend for the current platform per preference order.
    /// Returns null if no backend is available (shouldn't happen in practice).
    /// </summary>
    public static GraphicsBackend? SelectBestBackend()
    {
        foreach (var backend in PreferenceOrder)
        {
            if (GraphicsDevice.IsBackendSupported(backend))
                return backend;
        }
        return null;
    }

    /// <summary>Returns the CLI flag name for a backend (e.g., "--metal").</summary>
    public static string GetBackendFlag(GraphicsBackend backend) => backend switch
    {
        GraphicsBackend.Direct3D11 => "--d3d11",
        GraphicsBackend.Metal => "--metal",
        GraphicsBackend.Vulkan => "--vulkan",
        GraphicsBackend.OpenGL => "--opengl",
        GraphicsBackend.OpenGLES => "--opengles",
        _ => $"--{backend.ToString().ToLowerInvariant()}"
    };

    /// <summary>
    /// Tries to parse a backend from command-line arguments.
    /// Returns null if no backend flag was found.
    /// </summary>
    public static GraphicsBackend? ParseBackendArg(string[] args)
    {
        foreach (string arg in args)
        {
            switch (arg.ToLowerInvariant())
            {
                case "--d3d11":
                case "--d3d":
                    return GraphicsBackend.Direct3D11;
                case "--metal":
                    return GraphicsBackend.Metal;
                case "--vulkan":
                case "--vk":
                    return GraphicsBackend.Vulkan;
                case "--opengl":
                case "--gl":
                    return GraphicsBackend.OpenGL;
                case "--opengles":
                case "--gles":
                    return GraphicsBackend.OpenGLES;
            }
        }
        return null;
    }

    /// <summary>
    /// Creates a Veldrid GraphicsDevice from a Silk.NET window for the specified backend.
    /// The window must already be initialized (for OpenGL, GLContext must be current).
    /// </summary>
    public static GraphicsDevice Create(IWindow window, GraphicsBackend backend, GraphicsDeviceOptions options)
    {
        Vector2D<int> fbSize = window.FramebufferSize;

        if (backend == GraphicsBackend.OpenGL)
            return CreateOpenGL(window, options, fbSize);

        if (backend == GraphicsBackend.OpenGLES)
            return CreateOpenGLES(window, options, fbSize);

        // D3D11, Vulkan, Metal — all use swapchain from native window handle
        SwapchainSource swapchainSource = GetSwapchainSource(window);
        SwapchainDescription swapchainDesc = new SwapchainDescription(
            swapchainSource,
            (uint)fbSize.X,
            (uint)fbSize.Y,
            options.SwapchainDepthFormat,
            options.SyncToVerticalBlank);

        return backend switch
        {
            GraphicsBackend.Direct3D11 => GraphicsDevice.CreateD3D11(options, swapchainDesc),
            GraphicsBackend.Metal => GraphicsDevice.CreateMetal(options, swapchainDesc),
            GraphicsBackend.Vulkan => GraphicsDevice.CreateVulkan(options, swapchainDesc),
            _ => throw new ArgumentException($"Unsupported backend: {backend}")
        };
    }

    /// <summary>Whether the given backend requires an OpenGL context from the window.</summary>
    public static bool IsOpenGLBased(GraphicsBackend backend)
        => backend is GraphicsBackend.OpenGL or GraphicsBackend.OpenGLES;

    // ── Private implementation ──────────────────────────────────────────

    private static GraphicsDevice CreateOpenGL(IWindow window, GraphicsDeviceOptions options, Vector2D<int> fbSize)
    {
        var glContext = window.GLContext
            ?? throw new InvalidOperationException(
                "GLContext is null. Ensure WindowOptions.API is set for OpenGL context creation.");

        OpenGLPlatformInfo platformInfo = new OpenGLPlatformInfo(
            openGLContextHandle: glContext.Handle,
            getProcAddress: name => { try { return glContext.GetProcAddress(name); } catch { return IntPtr.Zero; } },
            makeCurrent: _ => glContext.MakeCurrent(),
            getCurrentContext: () => glContext.IsCurrent ? glContext.Handle : IntPtr.Zero,
            clearCurrentContext: () => glContext.Clear(),
            deleteContext: _ => { },
            swapBuffers: () => glContext.SwapBuffers(),
            setSyncToVerticalBlank: vsync => glContext.SwapInterval(vsync ? 1 : 0));

        var device = GraphicsDevice.CreateOpenGL(
            options, platformInfo, (uint)fbSize.X, (uint)fbSize.Y);

        // Transfer GL context ownership to Veldrid's execution thread
        glContext.Clear();
        return device;
    }

    private static GraphicsDevice CreateOpenGLES(IWindow window, GraphicsDeviceOptions options, Vector2D<int> fbSize)
    {
        // OpenGLES uses a SwapchainDescription (not OpenGLPlatformInfo like desktop GL)
        SwapchainSource swapchainSource = GetSwapchainSource(window);
        SwapchainDescription swapchainDesc = new SwapchainDescription(
            swapchainSource,
            (uint)fbSize.X,
            (uint)fbSize.Y,
            options.SwapchainDepthFormat,
            options.SyncToVerticalBlank);

        return GraphicsDevice.CreateOpenGLES(options, swapchainDesc);
    }

    private static SwapchainSource GetSwapchainSource(IWindow window)
    {
        var native = window.Native
            ?? throw new InvalidOperationException(
                "Window has no native handle. Ensure API = GraphicsAPI.None and window is initialized.");

        if (native.Win32.HasValue)
        {
            (nint hwnd, _, nint hinstance) = native.Win32.Value;
            return SwapchainSource.CreateWin32(hwnd, hinstance);
        }

        if (native.Cocoa.HasValue)
        {
            nint nsWindow = (nint)native.Cocoa.Value;
            return SwapchainSource.CreateNSWindow(nsWindow);
        }

        if (native.X11.HasValue)
        {
            var x11 = native.X11.Value;
            return SwapchainSource.CreateXlib((nint)x11.Display, (nint)x11.Window);
        }

        if (native.Wayland.HasValue)
        {
            var wl = native.Wayland.Value;
            return SwapchainSource.CreateWayland((nint)wl.Display, (nint)wl.Surface);
        }

        throw new PlatformNotSupportedException(
            "No supported native window handle found. " +
            $"Win32={native.Win32.HasValue}, Cocoa={native.Cocoa.HasValue}, " +
            $"X11={native.X11.HasValue}, Wayland={native.Wayland.HasValue}");
    }
}
