using System;
using System.Diagnostics;
using System.Linq;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using Veldrid;
using VeldridCompat = SilkyNvg.Rendering.Veldrid.VeldridCompat;

/// <summary>
/// Lightweight Veldrid + SilkyNvg example.
/// Auto-discovers the best available graphics backend and opens a window.
/// Override with: --d3d11, --metal, --vulkan, --opengl
/// </summary>
static class Program
{
    private const int WINDOW_WIDTH = 1000;
    private const int WINDOW_HEIGHT = 600;

    static void Main(string[] args)
    {
        // ── Platform & backend discovery ────────────────────────────────
        string platform = VeldridDeviceFactory.GetPlatformName();
        GraphicsBackend[] available = VeldridDeviceFactory.GetAvailableBackends();

        Console.WriteLine($"Platform:   {platform}");
        Console.WriteLine($"Available:  {string.Join(", ", available)}");

        // ── Backend selection ───────────────────────────────────────────
        GraphicsBackend backend;
        GraphicsBackend? requested = VeldridDeviceFactory.ParseBackendArg(args);

        if (requested.HasValue)
        {
            if (!available.Contains(requested.Value))
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine($"ERROR: Backend '{requested.Value}' is not available on this platform.");
                Console.Error.WriteLine($"Available: {string.Join(", ", available.Select(VeldridDeviceFactory.GetBackendFlag))}");
                Environment.Exit(1);
            }
            backend = requested.Value;
            Console.WriteLine($"Selected:   {backend} (from args)");
        }
        else
        {
            backend = VeldridDeviceFactory.SelectBestBackend()
                ?? throw new PlatformNotSupportedException("No graphics backend available!");
            Console.WriteLine($"Selected:   {backend} (auto)");
        }
        Console.WriteLine();

        // ── Window creation ────────────────────────────────────────────
        WindowOptions windowOptions = WindowOptions.Default;
        windowOptions.Size = new Vector2D<int>(WINDOW_WIDTH, WINDOW_HEIGHT);
        string veldridAssemblyName = typeof(GraphicsDevice).Assembly.GetName().Name!;
        string veldridVersion = typeof(GraphicsDevice).Assembly.GetName().Version?.ToString() ?? "?";
        windowOptions.Title = $"SilkyNvg [{veldridAssemblyName} {veldridVersion}] ({backend})";
        windowOptions.VSync = false;
        windowOptions.PreferredDepthBufferBits = 24;
        windowOptions.PreferredStencilBufferBits = 8;

        // OpenGL-based backends need Silk.NET to create the GL context;
        // others create their own swapchain from the native window handle.
        if (VeldridDeviceFactory.IsOpenGLBased(backend))
        {
            windowOptions.API = GraphicsAPI.Default;
            windowOptions.ShouldSwapAutomatically = false;
        }
        else
        {
            windowOptions.API = GraphicsAPI.None;
        }

        IWindow window = Window.Create(windowOptions);

        // ── Device creation & run ──────────────────────────────────────
        // Apple Silicon Metal doesn't support D24+S8; use D32+S8 on Metal.
        PixelFormat depthFormat = backend == GraphicsBackend.Metal
            ? PixelFormat.D32FloatS8UInt
            : VeldridCompat.DepthStencilD24S8;

        GraphicsDeviceOptions deviceOptions = new()
        {
            Debug = true,
            PreferDepthRangeZeroToOne = true,
            PreferStandardClipSpaceYDirection = true,
            SyncToVerticalBlank = false,
            SwapchainDepthFormat = depthFormat,
            // Improved binding model: uniform buffers bind to their declared slot directly,
            // without offset by vertex buffer count. Required for precompiled .vdshader bundles
            // where the MSL shader has [[buffer(0)]] for the first uniform.
            ResourceBindingModel = ResourceBindingModel.Improved
        };

        if (VeldridDeviceFactory.IsOpenGLBased(backend))
            RunOpenGLLoop(window, backend, deviceOptions);
        else
            RunStandardLoop(window, backend, deviceOptions);

        window.Dispose();
    }

    /// <summary>
    /// OpenGL path: manual event pump because Veldrid's execution thread owns the GL context.
    /// </summary>
    private static void RunOpenGLLoop(IWindow window, GraphicsBackend backend, GraphicsDeviceOptions deviceOptions)
    {
        window.Initialize();
        window.GLContext!.MakeCurrent();

        GraphicsDevice device = VeldridDeviceFactory.Create(window, backend, deviceOptions);
        device.WaitForIdle(); // Flush deferred resource creation

        Console.WriteLine($"Graphics backend: {device.BackendType}");
        PrintDiagnostics(window, device);

        using var app = new NvgDemoApp(device, window);

        var timer = Stopwatch.StartNew();
        double previousTime = 0;

        while (!window.IsClosing)
        {
            window.DoEvents();
            double currentTime = timer.Elapsed.TotalSeconds;
            double deltaTime = currentTime - previousTime;
            previousTime = currentTime;
            app.Render(deltaTime);
        }

        device.Dispose();
    }

    /// <summary>
    /// D3D11/Metal/Vulkan path: standard Silk.NET event loop.
    /// </summary>
    private static void RunStandardLoop(IWindow window, GraphicsBackend backend, GraphicsDeviceOptions deviceOptions)
    {
        GraphicsDevice device = null!;
        NvgDemoApp app = null!;

        window.Load += () =>
        {
            device = VeldridDeviceFactory.Create(window, backend, deviceOptions);
            Console.WriteLine($"Graphics backend: {device.BackendType}");
            PrintDiagnostics(window, device);
            app = new NvgDemoApp(device, window);
        };

        window.Render += deltaTime => app.Render(deltaTime);
        window.Resize += newSize => app.Resize(newSize);
        window.Closing += () =>
        {
            app.Dispose();
            device.Dispose();
        };

        window.Run();
    }

    private static void PrintDiagnostics(IWindow window, GraphicsDevice device)
    {
        uint fbW = device.SwapchainFramebuffer.Width;
        uint fbH = device.SwapchainFramebuffer.Height;
        var winSize = window.Size;
        Console.WriteLine($"Window:     {winSize.X}x{winSize.Y} logical, {fbW}x{fbH} pixels " +
                          $"(ratio: {(float)fbW / winSize.X:F2}x)");
    }
}
