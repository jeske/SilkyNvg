using System;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using NvgExample;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SilkyNvg;
using SilkyNvg.Rendering.Veldrid;
using Veldrid;

/// <summary>
/// Standalone Veldrid example for the SilkyNvg VeldridRenderer backend.
/// Uses Silk.NET windowing (same as the OpenGL example) for proper DPI awareness.
/// Creates a Veldrid GraphicsDevice from the native window handle.
/// </summary>
public static class Program
{
    private const int WINDOW_WIDTH = 1000;
    private const int WINDOW_HEIGHT = 600;

    private static IWindow appWindow = null!;
    private static GraphicsDevice graphicsDevice = null!;
    private static CommandList renderCommandList = null!;
    private static VeldridRenderer nvgRenderer = null!;
    private static Nvg nvgContext = null!;
    private static Demo demo = null!;

    private static PerformanceGraph frameTimeGraph = null!;
    private static PerformanceGraph cpuTimeGraph = null!;
    private static Stopwatch frameTimer = null!;
    private static double previousTimeSeconds;

    private static void Load()
    {
        // Create Veldrid GraphicsDevice from the Silk.NET window's native handle
        GraphicsDeviceOptions graphicsDeviceOptions = new GraphicsDeviceOptions {
            PreferDepthRangeZeroToOne = true,
            SyncToVerticalBlank = true,
            // 8-bit stencil required for non-convex path fill (stencil-then-cover)
            SwapchainDepthFormat = PixelFormat.D24_UNorm_S8_UInt
        };

        // Get native window handle for swapchain creation
        SwapchainSource swapchainSource = GetSwapchainSource(appWindow);
        Vector2D<int> framebufferSize = appWindow.FramebufferSize;

        SwapchainDescription swapchainDescription = new SwapchainDescription(
            swapchainSource,
            (uint)framebufferSize.X,
            (uint)framebufferSize.Y,
            graphicsDeviceOptions.SwapchainDepthFormat,
            graphicsDeviceOptions.SyncToVerticalBlank);

        graphicsDevice = GraphicsDevice.CreateD3D11(graphicsDeviceOptions, swapchainDescription);
        Console.WriteLine($"Graphics backend: {graphicsDevice.BackendType}");
        appWindow.Title = $"SilkyNvg Veldrid Backend Example ({graphicsDevice.BackendType})";

        // Diagnostic: compare window logical size vs framebuffer physical size
        uint framebufferPixelWidth = graphicsDevice.SwapchainFramebuffer.Width;
        uint framebufferPixelHeight = graphicsDevice.SwapchainFramebuffer.Height;
        Vector2D<int> windowLogicalSize = appWindow.Size;
        Console.WriteLine($"[VELDRID DIAG] Window logical size: {windowLogicalSize.X}x{windowLogicalSize.Y}");
        Console.WriteLine($"[VELDRID DIAG] Framebuffer pixel size: {framebufferPixelWidth}x{framebufferPixelHeight}");
        Console.WriteLine($"[VELDRID DIAG] Pixel ratio (fb/win): {(float)framebufferPixelWidth / windowLogicalSize.X:F3}x{(float)framebufferPixelHeight / windowLogicalSize.Y:F3}");

        // Create Veldrid command list
        renderCommandList = graphicsDevice.ResourceFactory.CreateCommandList();

        // Create our NVG renderer and context
        nvgRenderer = new VeldridRenderer(graphicsDevice);
        nvgRenderer.Create();
        nvgRenderer.SetActiveCommandList(renderCommandList);

        nvgContext = Nvg.Create(nvgRenderer);

        // Load the shared NvgExample demo (fonts, images, UI widgets)
        demo = new Demo(nvgContext);

        frameTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Fps, "Frame Time");
        cpuTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Ms, "CPU Time");

        frameTimer = Stopwatch.StartNew();
        previousTimeSeconds = 0;

        Console.WriteLine("Entering render loop. Close window to exit.");
    }

    private static void Render(double _)
    {
        // Timing
        double currentTimeSeconds = frameTimer!.Elapsed.TotalSeconds;
        double frameDeltaSeconds = currentTimeSeconds - previousTimeSeconds;
        previousTimeSeconds = currentTimeSeconds;

        // Begin GPU commands
        renderCommandList.Begin();
        renderCommandList.SetFramebuffer(graphicsDevice.SwapchainFramebuffer);
        renderCommandList.ClearColorTarget(0, new RgbaFloat(0.3f, 0.3f, 0.32f, 1.0f));
        renderCommandList.ClearDepthStencil(0f, 0);

        // NVG frame — use window size (logical) for NVG coordinates
        Vector2 windowSize = appWindow.Size.As<float>().ToSystem();
        nvgContext.BeginFrame(new SizeF(windowSize.X, windowSize.Y), 1.0f);

        // Draw the full NanoVG demo (eyes, widgets, color wheel, graphs, etc.)
        demo.Render(0, 0, windowSize.X, windowSize.Y, (float)currentTimeSeconds, false);

        // Performance graphs
        frameTimeGraph.Render(5.0f, 5.0f, nvgContext);
        cpuTimeGraph.Render(5.0f + 200.0f + 5.0f, 5.0f, nvgContext);

        nvgContext.EndFrame();

        double cpuTimeSeconds = frameTimer.Elapsed.TotalSeconds - currentTimeSeconds;

        frameTimeGraph.Update((float)frameDeltaSeconds);
        cpuTimeGraph.Update((float)cpuTimeSeconds);

        // Submit
        renderCommandList.End();
        graphicsDevice.SubmitCommands(renderCommandList);
        graphicsDevice.SwapBuffers();
    }

    private static void Close()
    {
        Console.WriteLine("Shutting down...");
        graphicsDevice.WaitForIdle();
        demo.Dispose();
        nvgContext.Dispose();
        nvgRenderer.Dispose();
        renderCommandList.Dispose();
        graphicsDevice.Dispose();
    }

    private static void Resize(Vector2D<int> newSize)
    {
        graphicsDevice.MainSwapchain.Resize((uint)newSize.X, (uint)newSize.Y);
    }

    public static void Main()
    {
        WindowOptions windowOptions = WindowOptions.Default;
        windowOptions.Size = new Vector2D<int>(WINDOW_WIDTH, WINDOW_HEIGHT);
        windowOptions.Title = "SilkyNvg Veldrid Backend Example";
        windowOptions.VSync = true;
        windowOptions.PreferredDepthBufferBits = 24;
        windowOptions.PreferredStencilBufferBits = 8;
        // Don't let Silk.NET create an OpenGL context — we'll use Veldrid
        windowOptions.API = GraphicsAPI.None;

        appWindow = Window.Create(windowOptions);
        appWindow.Load += Load;
        appWindow.Render += Render;
        appWindow.Closing += Close;
        appWindow.Resize += Resize;
        appWindow.Run();

        appWindow.Dispose();
    }

    /// <summary>
    /// Creates a Veldrid SwapchainSource from a Silk.NET window's native handle.
    /// Supports Win32 (Windows) — extend for other platforms as needed.
    /// </summary>
    private static SwapchainSource GetSwapchainSource(IWindow silkNetWindow)
    {
        if (silkNetWindow.Native == null) {
            throw new InvalidOperationException(
                "Silk.NET window has no native handle. " +
                "Ensure the window is created with API = GraphicsAPI.None and is fully initialized.");
        }

        if (silkNetWindow.Native.Win32.HasValue) {
            (nint hwnd, nint hdc, nint hinstance) = silkNetWindow.Native.Win32.Value;
            return SwapchainSource.CreateWin32(hwnd, hinstance);
        }

        // Add X11/Wayland/macOS support here if needed
        throw new PlatformNotSupportedException(
            $"No supported native window handle found. " +
            $"Win32={silkNetWindow.Native.Win32.HasValue}. " +
            $"Only Windows (Win32) is currently supported for the Veldrid example.");
    }
}