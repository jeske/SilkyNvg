#nullable enable
using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Android.Content;
using Android.Util;
using NvgExample;
using SilkyNvg;
using SilkyNvg.Rendering.Veldrid;
using Veldrid;
using VeldridCompat = SilkyNvg.Rendering.Veldrid.VeldridCompat;

namespace Veldrid_Android_Example;

/// <summary>
/// Creates a Veldrid GraphicsDevice from an Android Surface (via Vulkan),
/// initializes the SilkyNvg VeldridRenderer, and runs the NanoVG demo render loop
/// on a dedicated background thread.
/// </summary>
public class VeldridAndroidRenderer
{
    private const string LOG_TAG = "NvgVeldrid";

    private GraphicsDevice? vulkanGraphicsDevice;
    private CommandList? renderCommandList;
    private VeldridRenderer? nvgVeldridRenderer;
    private Nvg? nvgContext;
    private Demo? nvgDemo;

    private PerformanceGraph? frameTimeGraph;
    private PerformanceGraph? cpuTimeGraph;
    private Stopwatch? frameTimer;
    private double previousFrameTimeSeconds;

    private Thread? renderLoopThread;
    private volatile bool isRenderLoopRunning;
    private volatile bool isDisposed;

    private uint surfacePixelWidth;
    private uint surfacePixelHeight;

    /// <summary>
    /// Initializes the Vulkan GraphicsDevice from an Android Surface.
    /// Must be called from the UI thread when the surface is available (SurfaceCreated).
    /// </summary>
    public void InitializeFromSurface(Android.Views.Surface androidSurface, Context androidContext)
    {
        if (isDisposed) {
            throw new ObjectDisposedException(nameof(VeldridAndroidRenderer),
                "Cannot initialize — renderer has already been disposed. Create a new instance.");
        }

        // Get the Java Surface jobject handle — ppy.Veldrid expects the Java Surface reference,
        // NOT an ANativeWindow pointer. It handles the native window conversion internally.
        IntPtr javaSurfaceHandle = androidSurface.Handle;
        if (javaSurfaceHandle == IntPtr.Zero) {
            throw new InvalidOperationException(
                "Android Surface.Handle is null. The Surface may not be valid.");
        }

        // Query surface dimensions via ANativeWindow (for NVG frame sizing)
        IntPtr nativeWindowForSizing = ANativeWindow_fromSurface(
            Java.Interop.JniEnvironment.EnvironmentPointer,
            javaSurfaceHandle);
        if (nativeWindowForSizing != IntPtr.Zero) {
            surfacePixelWidth = (uint)ANativeWindow_getWidth(nativeWindowForSizing);
            surfacePixelHeight = (uint)ANativeWindow_getHeight(nativeWindowForSizing);
            ANativeWindow_release(nativeWindowForSizing);
        }
        Log.Info(LOG_TAG, $"Surface size: {surfacePixelWidth}x{surfacePixelHeight}");

        // Create Veldrid GraphicsDevice with Vulkan backend
        GraphicsDeviceOptions vulkanDeviceOptions = new GraphicsDeviceOptions {
            PreferDepthRangeZeroToOne = true,
            SyncToVerticalBlank = true,
            // 8-bit stencil required for non-convex path fill (stencil-then-cover)
            SwapchainDepthFormat = VeldridCompat.DepthStencilD24S8
        };

        IntPtr jniEnvironmentPointer = Java.Interop.JniEnvironment.EnvironmentPointer;
        Log.Info(LOG_TAG, $"Creating SwapchainSource: javaSurface=0x{javaSurfaceHandle:X}, jniEnv=0x{jniEnvironmentPointer:X}");

        SwapchainSource androidSwapchainSource = SwapchainSource.CreateAndroidSurface(
            javaSurfaceHandle, jniEnvironmentPointer);

        SwapchainDescription swapchainDescription = new SwapchainDescription(
            androidSwapchainSource,
            surfacePixelWidth,
            surfacePixelHeight,
            vulkanDeviceOptions.SwapchainDepthFormat,
            vulkanDeviceOptions.SyncToVerticalBlank);

        Log.Info(LOG_TAG, "Calling GraphicsDevice.CreateVulkan...");
        vulkanGraphicsDevice = GraphicsDevice.CreateVulkan(vulkanDeviceOptions, swapchainDescription);
        Log.Info(LOG_TAG, $"Veldrid GraphicsDevice created: {vulkanGraphicsDevice.BackendType}");

        // Create command list and NVG renderer
        renderCommandList = vulkanGraphicsDevice.ResourceFactory.CreateCommandList();

        nvgVeldridRenderer = new VeldridRenderer(vulkanGraphicsDevice);
        nvgVeldridRenderer.Create();
        nvgVeldridRenderer.SetActiveCommandList(renderCommandList);

        nvgContext = Nvg.Create(nvgVeldridRenderer);

        // Load the shared NvgExample demo (fonts, images, UI widgets)
        // Demo.SetFileLoader() must have been called BEFORE this point (done in MainActivity.OnCreate)
        nvgDemo = new Demo(nvgContext);

        frameTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Fps, "Frame Time");
        cpuTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Ms, "CPU Time");

        frameTimer = Stopwatch.StartNew();
        previousFrameTimeSeconds = 0;

    }

    /// <summary>
    /// Starts the render loop on a dedicated background thread.
    /// The touchMouseX/Y delegates provide the current touch position mapped to NVG coordinates.
    /// </summary>
    public void StartRenderLoop(Func<float> getTouchMouseX, Func<float> getTouchMouseY)
    {
        if (vulkanGraphicsDevice == null) {
            throw new InvalidOperationException(
                "Cannot start render loop — InitializeFromSurface() has not been called yet.");
        }

        isRenderLoopRunning = true;
        renderLoopThread = new Thread(() => RenderLoopBody(getTouchMouseX, getTouchMouseY)) {
            Name = "NvgVeldridRenderThread",
            IsBackground = true
        };
        renderLoopThread.Start();
    }

    /// <summary>
    /// Notifies the renderer that the surface has been resized.
    /// Called from ISurfaceHolderCallback.SurfaceChanged on the UI thread.
    /// </summary>
    public void HandleSurfaceResize(uint newPixelWidth, uint newPixelHeight)
    {
        surfacePixelWidth = newPixelWidth;
        surfacePixelHeight = newPixelHeight;
        vulkanGraphicsDevice?.MainSwapchain?.Resize(newPixelWidth, newPixelHeight);
    }

    /// <summary>
    /// Stops the render loop and disposes all Veldrid/NVG resources.
    /// Safe to call multiple times.
    /// </summary>
    public void StopAndDispose()
    {
        if (isDisposed) {
            return;
        }
        isDisposed = true;
        isRenderLoopRunning = false;

        // Wait for render thread to finish
        renderLoopThread?.Join(TimeSpan.FromSeconds(2));

        vulkanGraphicsDevice?.WaitForIdle();

        nvgDemo?.Dispose();
        nvgContext?.Dispose();
        nvgVeldridRenderer?.Dispose();
        renderCommandList?.Dispose();
        vulkanGraphicsDevice?.Dispose();

        nvgDemo = null;
        nvgContext = null;
        nvgVeldridRenderer = null;
        renderCommandList = null;
        vulkanGraphicsDevice = null;
    }

    private void RenderLoopBody(Func<float> getTouchMouseX, Func<float> getTouchMouseY)
    {
        Log.Info(LOG_TAG, "Render loop started");

        while (isRenderLoopRunning) {
            if (vulkanGraphicsDevice == null || renderCommandList == null ||
                nvgContext == null || nvgDemo == null || frameTimer == null ||
                frameTimeGraph == null || cpuTimeGraph == null) {
                break;
            }

            // Timing
            double currentTimeSeconds = frameTimer.Elapsed.TotalSeconds;
            double frameDeltaSeconds = currentTimeSeconds - previousFrameTimeSeconds;
            previousFrameTimeSeconds = currentTimeSeconds;

            // Read touch position (thread-safe via volatile reads in the delegates)
            float currentTouchX = getTouchMouseX();
            float currentTouchY = getTouchMouseY();

            // Use desktop example dimensions (1000x600) for consistent layout
            const float DESKTOP_DEMO_WIDTH = 1000f;
            const float DESKTOP_DEMO_HEIGHT = 600f;
            float nvgFrameWidth = DESKTOP_DEMO_WIDTH;
            float nvgFrameHeight = DESKTOP_DEMO_HEIGHT;

            // Calculate viewport to center the 1000x600 content without stretching
            float scaleX = surfacePixelWidth / DESKTOP_DEMO_WIDTH;
            float scaleY = surfacePixelHeight / DESKTOP_DEMO_HEIGHT;
            float uniformScale = Math.Min(scaleX, scaleY);
            
            uint scaledWidth = (uint)(DESKTOP_DEMO_WIDTH * uniformScale);
            uint scaledHeight = (uint)(DESKTOP_DEMO_HEIGHT * uniformScale);
            uint viewportX = (surfacePixelWidth - scaledWidth) / 2;
            uint viewportY = (surfacePixelHeight - scaledHeight) / 2;

            // Begin GPU commands
            renderCommandList.Begin();
            renderCommandList.SetFramebuffer(vulkanGraphicsDevice.SwapchainFramebuffer);
            renderCommandList.ClearColorTarget(0, new RgbaFloat(0.0f, 0.0f, 0.0f, 1.0f)); // Black letterbox bars
            
            // Set viewport to center the content
            renderCommandList.SetViewport(0, new Viewport(viewportX, viewportY, scaledWidth, scaledHeight, 0f, 1f));
            renderCommandList.SetScissorRect(0, viewportX, viewportY, scaledWidth, scaledHeight);
            
            renderCommandList.ClearDepthStencil(0f, 0);

            // NVG frame — fully qualify SizeF to avoid ambiguity with Android.Util.SizeF
            nvgContext.BeginFrame(new System.Drawing.SizeF(nvgFrameWidth, nvgFrameHeight), 1.0f);

            // Draw the full NanoVG demo (eyes, widgets, color wheel, graphs, etc.)
            nvgDemo.Render(currentTouchX, currentTouchY, nvgFrameWidth, nvgFrameHeight,
                (float)currentTimeSeconds, blowup: false);

            // Performance graphs
            frameTimeGraph.Render(5.0f, 5.0f, nvgContext);
            cpuTimeGraph.Render(5.0f + 200.0f + 5.0f, 5.0f, nvgContext);

            nvgContext.EndFrame();

            double cpuTimeSeconds = frameTimer.Elapsed.TotalSeconds - currentTimeSeconds;

            frameTimeGraph.Update((float)frameDeltaSeconds);
            cpuTimeGraph.Update((float)cpuTimeSeconds);

            // Submit
            renderCommandList.End();
            vulkanGraphicsDevice.SubmitCommands(renderCommandList);
            vulkanGraphicsDevice.SwapBuffers();
        }

        Log.Info(LOG_TAG, "Render loop stopped");
    }

    // --- Native Android window functions (libandroid.so) ---

    [DllImport("android")]
    private static extern IntPtr ANativeWindow_fromSurface(IntPtr jniEnvPointer, IntPtr javaSurfaceObject);

    [DllImport("android")]
    private static extern void ANativeWindow_release(IntPtr nativeWindowHandle);

    [DllImport("android")]
    private static extern int ANativeWindow_getWidth(IntPtr nativeWindowHandle);

    [DllImport("android")]
    private static extern int ANativeWindow_getHeight(IntPtr nativeWindowHandle);
}