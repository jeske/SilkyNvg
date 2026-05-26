# Veldrid Example Refactoring Plan

## Goal

Lightweight Veldrid + SilkyNvg example. Get a window up showing NVG drawing commands with minimum fuss on any platform. Auto-discovers the best available backend.

## Backend Preference Order

D3D11 > Metal > Vulkan > OpenGL > OpenGLES

## Proposed File Structure

```
samples/Veldrid Example/
├── Program.cs                    # Entry point: discover, select, run
├── VeldridDeviceFactory.cs       # Platform detection + GraphicsDevice creation
└── NvgDemoApp.cs                 # NVG renderer, render loop, input, cleanup
```

## Startup Behavior

**No arguments:**
- Detect platform, enumerate available backends
- Pick the best one per preference order
- Print platform + available + selected to console
- Open window, run demo

**With `--vulkan`/`--metal`/`--d3d`/`--gl` argument:**
- Detect platform, enumerate available backends
- If requested backend is available → use it
- If not → print error with available list, exit(1)

## VeldridDeviceFactory.cs

```csharp
static class VeldridDeviceFactory
{
    // Backend preference: D3D11 > Metal > Vulkan > OpenGL > OpenGLES
    static readonly GraphicsBackend[] PreferenceOrder = { ... };
    
    static string GetPlatformName();
    static GraphicsBackend[] GetAvailableBackends();
    static GraphicsBackend SelectBestBackend();
    static GraphicsDevice Create(IWindow window, GraphicsBackend backend, GraphicsDeviceOptions options);
}
```

`GetSwapchainSource` handles:
- `window.Native.Win32` → `SwapchainSource.CreateWin32(hwnd, hinstance)`
- `window.Native.Cocoa` → `SwapchainSource.CreateNSWindow(nsWindow)`
- `window.Native.X11` → `SwapchainSource.CreateXlib(display, window)`

## NvgDemoApp.cs

```csharp
class NvgDemoApp : IDisposable
{
    NvgDemoApp(GraphicsDevice device, IWindow window);
    void Render(double deltaTime);
    void Resize(Vector2D<int> newSize);
    void Dispose();
}
```

## Program.cs

~40 lines. Parse args, print discovery, create window, create device, run app.

## Implementation Steps

1. Create `VeldridDeviceFactory.cs` with platform detection, backend enumeration, device creation (all platforms)
2. Create `NvgDemoApp.cs` — extract NVG/render/input/cleanup from current Program.cs
3. Rewrite `Program.cs` — minimal orchestration with discovery output
4. Test on macOS (Metal or Vulkan via MoltenVK)
