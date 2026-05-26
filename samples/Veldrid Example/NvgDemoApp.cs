using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Numerics;
using System.Reflection;
using NvgExample;
using Silk.NET.Input;
using Silk.NET.Maths;
using Silk.NET.Windowing;
using SilkyNvg;
using SilkyNvg.Rendering.Veldrid;
using Veldrid;
using VeldridCompat = SilkyNvg.Rendering.Veldrid.VeldridCompat;

/// <summary>
/// Owns the NVG renderer lifecycle: setup, render loop, input handling, cleanup.
/// Decoupled from window/device creation — receives a ready-to-use GraphicsDevice.
/// </summary>
sealed class NvgDemoApp : IDisposable
{
    private readonly GraphicsDevice _device;
    private readonly IWindow _window;
    private readonly CommandList _commandList;
    private readonly VeldridRenderer _nvgRenderer;
    private readonly Nvg _nvg;
    private readonly Demo _demo;
    private readonly PerformanceGraph _frameTimeGraph;
    private readonly PerformanceGraph _cpuTimeGraph;
    private readonly Stopwatch _timer;

    private double _previousTime;
    private float _mouseX, _mouseY;
    private bool _blowup;

    public NvgDemoApp(GraphicsDevice device, IWindow window)
    {
        _device = device;
        _window = window;

        _commandList = device.ResourceFactory.CreateCommandList();

        _nvgRenderer = new VeldridRenderer(device);
        _nvgRenderer.Create();
        _nvgRenderer.SetActiveCommandList(_commandList);

        _nvg = Nvg.Create(_nvgRenderer);

        // Resolve asset directory (fonts/, images/) relative to the NvgExample project
        string assetDir = FindAssetDirectory();
        Demo.SetFileLoader(path => File.ReadAllBytes(Path.Combine(assetDir, path)));
        _demo = new Demo(_nvg);

        _frameTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Fps, "Frame Time");
        _cpuTimeGraph = new PerformanceGraph(PerformanceGraph.GraphRenderStyle.Ms, "CPU Time");

        _timer = Stopwatch.StartNew();
        _previousTime = 0;

        // Wire input
        IInputContext input = window.CreateInput();
        foreach (IKeyboard kb in input.Keyboards)
            kb.KeyDown += OnKeyDown;
        foreach (IMouse mouse in input.Mice)
            mouse.MouseMove += OnMouseMove;
    }

    /// <summary>Render one frame. Called from the event loop.</summary>
    public void Render(double deltaTime)
    {
        double currentTime = _timer.Elapsed.TotalSeconds;
        double frameDelta = deltaTime > 0 ? deltaTime : currentTime - _previousTime;
        _previousTime = currentTime;

        _commandList.Begin();
        _commandList.SetFramebuffer(_device.SwapchainFramebuffer);
        _commandList.ClearColorTarget(0, new RgbaFloat(0.3f, 0.3f, 0.32f, 1.0f));
        _commandList.ClearDepthStencil(1f, 0);

        Vector2 winSize = _window.Size.As<float>().ToSystem();
        _nvg.BeginFrame(new SizeF(winSize.X, winSize.Y), 1.0f);

        _demo.Render(_mouseX, _mouseY, winSize.X, winSize.Y, (float)currentTime, _blowup);

        _frameTimeGraph.Render(5.0f, 5.0f, _nvg);
        _cpuTimeGraph.Render(5.0f + 200.0f + 5.0f, 5.0f, _nvg);

        _nvg.EndFrame();

        double cpuTime = _timer.Elapsed.TotalSeconds - currentTime;
        _frameTimeGraph.Update((float)frameDelta);
        _cpuTimeGraph.Update((float)cpuTime);

        _commandList.End();
        _device.SubmitCommands(_commandList);
        _device.SwapBuffers();
    }

    /// <summary>Handle window resize.</summary>
    public void Resize(Vector2D<int> newSize)
    {
        _device.MainSwapchain.Resize((uint)newSize.X, (uint)newSize.Y);
    }

    public void Dispose()
    {
        _timer.Stop();
        Console.WriteLine($"Average frame time: {_frameTimeGraph.GraphAverage * 1000.0f:F1} ms");
        Console.WriteLine($"Average CPU time:   {_cpuTimeGraph.GraphAverage * 1000.0f:F1} ms");

        _device.WaitForIdle();
        _demo.Dispose();
        _nvg.Dispose();
        _nvgRenderer.Dispose();
        _commandList.Dispose();
    }

    private void OnKeyDown(IKeyboard keyboard, Key key, int scancode)
    {
        if (key == Key.Escape)
            _window.Close();
        else if (key == Key.Space)
            _blowup = !_blowup;
    }

    private void OnMouseMove(IMouse mouse, Vector2 pos)
    {
        _mouseX = pos.X;
        _mouseY = pos.Y;
    }

    /// <summary>
    /// Finds the NvgExample asset directory (contains fonts/ and images/).
    /// Searches upward from the executable location for samples/NvgExample/.
    /// </summary>
    private static string FindAssetDirectory()
    {
        // Try relative to the source file location (works with dotnet run from repo root)
        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NvgExample"),
            Path.Combine(Directory.GetCurrentDirectory(), "samples", "NvgExample"),
            Path.Combine(Directory.GetCurrentDirectory(), "..", "NvgExample"),
            Directory.GetCurrentDirectory(), // maybe already in the right directory
        ];

        foreach (string candidate in candidates)
        {
            string fullPath = Path.GetFullPath(candidate);
            if (Directory.Exists(Path.Combine(fullPath, "fonts")) &&
                Directory.Exists(Path.Combine(fullPath, "images")))
            {
                return fullPath;
            }
        }

        throw new FileNotFoundException(
            "Could not find NvgExample asset directory (fonts/ + images/). " +
            $"Searched from: {Directory.GetCurrentDirectory()}");
    }
}
