using System;
using System.Diagnostics;
using System.Drawing;
using NvgExample;
using SilkyNvg;
using SilkyNvg.Rendering.Veldrid;
using Veldrid;
using Veldrid.Sdl2;
using Veldrid.StartupUtilities;

/// <summary>
/// Standalone Veldrid example for the SilkyNvg VeldridRenderer backend.
/// Uses the shared NvgExample Demo class (same as the OpenGL example).
/// No dependency on Arcane.Client — just Veldrid + SilkyNvg.
/// </summary>
public static class Program
{
    private const int WINDOW_WIDTH = 1000;
    private const int WINDOW_HEIGHT = 600;

    public static void Main()
    {
        // Create window + GraphicsDevice (Veldrid handles SDL2 internally)
        WindowCreateInfo windowCreateInfo = new WindowCreateInfo {
            X = 100,
            Y = 100,
            WindowWidth = WINDOW_WIDTH,
            WindowHeight = WINDOW_HEIGHT,
            WindowTitle = "SilkyNvg Veldrid Backend Example"
        };

        GraphicsDeviceOptions graphicsDeviceOptions = new GraphicsDeviceOptions {
            PreferStandardClipSpaceYDirection = true,
            PreferDepthRangeZeroToOne = true,
            SyncToVerticalBlank = true,
            // 8-bit stencil required for non-convex path fill (stencil-then-cover)
            SwapchainDepthFormat = PixelFormat.D24_UNorm_S8_UInt
        };

        VeldridStartup.CreateWindowAndGraphicsDevice(
            windowCreateInfo,
            graphicsDeviceOptions,
            out Sdl2Window appWindow,
            out GraphicsDevice graphicsDevice);

        Console.WriteLine($"Graphics backend: {graphicsDevice.BackendType}");

        // Create Veldrid command list
        CommandList renderCommandList = graphicsDevice.ResourceFactory.CreateCommandList();

        // Create our NVG renderer and context
        VeldridRenderer nvgRenderer = new VeldridRenderer(graphicsDevice);
        nvgRenderer.Create();
        nvgRenderer.SetActiveCommandList(renderCommandList); // Only need to call once

        Nvg nvgContext = Nvg.Create(nvgRenderer);

        // Load the shared NvgExample demo (fonts, images, UI widgets)
        Demo demo = new Demo(nvgContext);

        // Performance graphs
        PerformanceGraph frameTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Fps, "Frame Time");
        PerformanceGraph cpuTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Ms, "CPU Time");

        Stopwatch frameTimer = Stopwatch.StartNew();
        double previousTimeSeconds = 0;

        Console.WriteLine("Entering render loop. Close window to exit.");

        // Render loop
        while (appWindow.Exists) {
            appWindow.PumpEvents();

            // Timing
            double currentTimeSeconds = frameTimer.Elapsed.TotalSeconds;
            double frameDeltaSeconds = currentTimeSeconds - previousTimeSeconds;
            previousTimeSeconds = currentTimeSeconds;

            // Begin GPU commands
            renderCommandList.Begin();
            renderCommandList.SetFramebuffer(graphicsDevice.SwapchainFramebuffer);
            renderCommandList.ClearColorTarget(0, new RgbaFloat(0.3f, 0.3f, 0.32f, 1.0f));
            renderCommandList.ClearDepthStencil(0f, 0);

            // NVG frame
            float viewWidth = appWindow.Width;
            float viewHeight = appWindow.Height;
            nvgContext.BeginFrame(new SizeF(viewWidth, viewHeight), 1.0f);

            // Draw the full NanoVG demo (eyes, widgets, color wheel, graphs, etc.)
            demo.Render(0, 0, viewWidth, viewHeight, (float)currentTimeSeconds, false);

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

        // Cleanup
        Console.WriteLine("Shutting down...");
        graphicsDevice.WaitForIdle();
        demo.Dispose();
        nvgContext.Dispose();
        nvgRenderer.Dispose();
        renderCommandList.Dispose();
        graphicsDevice.Dispose();
    }
}