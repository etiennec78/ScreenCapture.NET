using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace ScreenCapture.NET;

/// <summary>
/// Represents a <see cref="IScreenCaptureService"/> using the <see cref="WaylandScreenCapture"/>.
/// </summary>
public class WaylandScreenCaptureService : IScreenCaptureService
{
    #region Properties & Fields

    private readonly Dictionary<Display, WaylandScreenCapture> _screenCaptures = new();

    private bool _isDisposed;
    private GraphicsCard _dummyGraphicsCard = new GraphicsCard(0, "Generic Wayland Graphics Card", 0, 0);

    #endregion

    #region Constructors

    /// <summary>
    /// Initializes a new instance of the <see cref="WaylandScreenCaptureService"/> class.
    /// </summary>
    public WaylandScreenCaptureService()
    { }

    ~WaylandScreenCaptureService() => Dispose();

    #endregion

    #region Methods


    /// <inheritdoc />
    public IEnumerable<GraphicsCard> GetGraphicsCards()
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);
        return new[] { _dummyGraphicsCard };
    }

    /// <inheritdoc />
    public IEnumerable<Display> GetDisplays(GraphicsCard graphicsCard)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);
    }

    /// <inheritdoc />
    IScreenCapture IScreenCaptureService.GetScreenCapture(Display display) => GetScreenCapture(display);

    /// <inheritdoc cref="IScreenCaptureService.GetScreenCapture"/>
    public WaylandScreenCapture GetScreenCapture(Display display)
    {
        if (_isDisposed) throw new ObjectDisposedException(GetType().FullName);

        if (!_screenCaptures.TryGetValue(display, out WaylandScreenCapture? screenCapture))
            _screenCaptures.Add(display, screenCapture = new WaylandScreenCapture(display));
        return screenCapture;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_isDisposed) return;

        foreach (WaylandScreenCapture screenCapture in _screenCaptures.Values)
            screenCapture.Dispose();
        _screenCaptures.Clear();

        GC.SuppressFinalize(this);

        _isDisposed = true;
    }

    #endregion
}
