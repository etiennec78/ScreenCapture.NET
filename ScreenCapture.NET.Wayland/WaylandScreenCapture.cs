using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using HPPH;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a ScreenCapture using XDG Desktop Portal with Wayland over DBus via XDG Desktop Portal.
/// </summary>
public sealed class WaylandScreenCapture : AbstractScreenCapture<ColorBGRA>
{
    #region Properties & Fields

    private readonly object _captureLock = new();

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WaylandScreenCapture"/> class.
    /// </summary>
    /// <param name="display">The <see cref="Display"/> to duplicate.</param>
    internal WaylandScreenCapture(Display display)
        : base(display)
    {
        Restart();
    }

    #endregion

    #region Methods

    /// <inheritdoc />
    protected override bool PerformScreenCapture()
    {
        lock (_captureLock)
        {
            return true;
        }
    }

    /// <inheritdoc />
    protected override void PerformCaptureZoneUpdate(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        using IDisposable @lock = captureZone.Lock();
        {
            if (captureZone.DownscaleLevel == 0)
                CopyZone(captureZone, buffer);
            else
                DownscaleZone(captureZone, buffer);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void CopyZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA>.Wrap(Data, Display.Width, Display.Height, _image.bytes_per_line)[captureZone.X, captureZone.Y, captureZone.Width, captureZone.Height]
        .CopyTo(MemoryMarshal.Cast<byte, ColorBGRA>(buffer));
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void DownscaleZone(CaptureZone<ColorBGRA> captureZone, Span<byte> buffer)
    {
        RefImage<ColorBGRA> source = RefImage<ColorBGRA>.Wrap(Data, Display.Width, Display.Height, _image.bytes_per_line)[captureZone.X, captureZone.Y, captureZone.UnscaledWidth, captureZone.UnscaledHeight];
        Span<ColorBGRA> target = MemoryMarshal.Cast<byte, ColorBGRA>(buffer);

        int blockSize = 1 << captureZone.DownscaleLevel;

        int width = captureZone.Width;
        int height = captureZone.Height;

        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                target[(y * width) + x] = source[x * blockSize, y * blockSize, blockSize, blockSize].Average();
    }

    /// <inheritdoc />
    public override void Restart()
    {
        base.Restart();

        lock (_captureLock)
        {
            DisposeDisplay();
        }
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        lock (_captureLock)
        {
            try { DisposeDisplay(); }
            catch { /**/ }
        }
    }

    private void DisposeDisplay()
    {
    }

    #endregion
}
