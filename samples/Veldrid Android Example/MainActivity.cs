#nullable enable
using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using System;
using System.IO;

namespace Veldrid_Android_Example;

/// <summary>
/// Android Activity that hosts a SurfaceView for Veldrid rendering.
/// Implements ISurfaceHolderCallback to manage the native surface lifecycle,
/// and maps touch events to mouse coordinates for the NanoVG demo.
/// </summary>
[Activity(
    Name = "com.arcane.veldrid.android.example.MainActivity",
    Label = "SilkyNvg Veldrid",
    MainLauncher = true,
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.KeyboardHidden,
    Theme = "@android:style/Theme.NoTitleBar.Fullscreen")]
public class MainActivity : Activity, ISurfaceHolderCallback
{
    private SurfaceView renderSurfaceView = null!;
    private VeldridAndroidRenderer veldridRenderer = null!;

    // Touch position mapped to NVG "mouse" coordinates
    private float touchMouseX;
    private float touchMouseY;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Wire up Demo's file loader to read from Android AssetManager BEFORE any Demo construction
        NvgExample.Demo.SetFileLoader(LoadAssetFileBytes);

        // Create a SurfaceView for Veldrid to render into
        renderSurfaceView = new SurfaceView(this);
        renderSurfaceView.Holder!.AddCallback(this);
        SetContentView(renderSurfaceView);

        veldridRenderer = new VeldridAndroidRenderer();
    }

    /// <summary>
    /// Loads a file from Android's AssetManager as a byte array.
    /// The Demo class uses paths like "./fonts/Roboto-Regular.ttf" and "./images/image0.jpg".
    /// We strip the leading "./" to get the AssetManager-relative path.
    /// </summary>
    private byte[] LoadAssetFileBytes(string relativeFilePath)
    {
        // Strip leading "./" that Demo.cs uses for desktop-relative paths
        string assetPath = relativeFilePath;
        if (assetPath.StartsWith("./", StringComparison.Ordinal)) {
            assetPath = assetPath.Substring(2);
        }

        using Stream assetStream = Assets!.Open(assetPath);
        using MemoryStream memoryBuffer = new MemoryStream();
        assetStream.CopyTo(memoryBuffer);
        return memoryBuffer.ToArray();
    }

    // --- ISurfaceHolderCallback ---

    public void SurfaceCreated(ISurfaceHolder surfaceHolder)
    {
        Android.Util.Log.Info("NvgVeldrid", "SurfaceCreated — initializing Veldrid GraphicsDevice");
        veldridRenderer.InitializeFromSurface(surfaceHolder.Surface!, this);
        veldridRenderer.StartRenderLoop(
            () => touchMouseX,
            () => touchMouseY);
    }

    public void SurfaceChanged(ISurfaceHolder surfaceHolder, Android.Graphics.Format format, int width, int height)
    {
        Android.Util.Log.Info("NvgVeldrid", $"SurfaceChanged: {width}x{height} format={format}");
        veldridRenderer.HandleSurfaceResize((uint)width, (uint)height);
    }

    public void SurfaceDestroyed(ISurfaceHolder surfaceHolder)
    {
        Android.Util.Log.Info("NvgVeldrid", "SurfaceDestroyed — stopping render loop and disposing");
        veldridRenderer.StopAndDispose();
    }

    // --- Touch → Mouse mapping ---

    public override bool OnTouchEvent(MotionEvent? touchEvent)
    {
        if (touchEvent == null) {
            return base.OnTouchEvent(touchEvent);
        }

        switch (touchEvent.Action) {
            case MotionEventActions.Down:
            case MotionEventActions.Move:
                touchMouseX = touchEvent.GetX();
                touchMouseY = touchEvent.GetY();
                break;
        }

        return true;
    }

    protected override void OnDestroy()
    {
        veldridRenderer.StopAndDispose();
        base.OnDestroy();
    }
}