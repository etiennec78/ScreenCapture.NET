using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Tmds.DBus.Protocol;
using Tmds.DBus.SourceGenerator;

namespace ScreenCapture.NET.Wayland;

/// <summary>
/// Manage the communication with XDG Desktop Portal over D-Bus for ScreenCast under Wayland.
/// </summary>
public sealed class XDGPortalManager : IAsyncDisposable {
  private const string _destination = "org.freedesktop.portal.Desktop";
  private const string _path = "/org/freedesktop/portal/desktop";
  private static readonly IReadOnlyDictionary<string, VariantValue> s_emptyResults = new Dictionary<string, VariantValue>();

  [Flags]
  private enum ScreenCastSourceType : uint {
    Monitor = 1,
    Window = 2,
    Virtual = 4,
    All = Monitor | Window | Virtual
  }

  private enum CursorMode : uint {
    Hidden = 1,
    Embedded = 2,
    Metadata = 4
  }

  private OrgFreedesktopPortalScreenCastProxy? _screenCast;
  private string? _senderName;
  private Connection? _connection;
  private ObjectPath _sessionHandle;
  private int _requestCounter = 0;
  private bool _sourcesSelected = false;
  private Dictionary<string, VariantValue>? _startResults;
  private bool _isDisposed = false;

  /// <summary>
  /// Store portal data after the ScreenCast session has been accepted.
  /// </summary>
  public IReadOnlyDictionary<string, VariantValue> StartResults => _startResults ?? s_emptyResults;

  private async Task<Dictionary<string, VariantValue>> RunRequestAndWaitAsync(
    string expectedReqPath,
    Func<Task<ObjectPath>> callAsync,
    string canceledMessage,
    CancellationToken cancellationToken = default)
  {
    var tcs = new TaskCompletionSource<(uint responseCode, Dictionary<string, VariantValue> results)>(TaskCreationOptions.RunContinuationsAsynchronously);

    using var registration = cancellationToken.Register(() => tcs.TrySetCanceled(cancellationToken));

    Action<Exception?, (uint responseCode, Dictionary<string, VariantValue> results)> onResponse = (ex, response) =>
    {
      if (ex != null) {
        tcs.TrySetException(ex);
        return;
      }

      tcs.TrySetResult(response);
    };

    var reqProxy = new OrgFreedesktopPortalRequestProxy(_connection!, _destination, expectedReqPath);
    using IDisposable watcher = await reqProxy.WatchResponseAsync(onResponse).ConfigureAwait(false);

    ObjectPath actualReqPath = await callAsync().ConfigureAwait(false);

    if (actualReqPath.ToString() != expectedReqPath) {
      throw new InvalidOperationException($"Portal returned unexpected request path. Expected {expectedReqPath}, got {actualReqPath}");
    }

    var (responseCode, results) = await tcs.Task.ConfigureAwait(false);

    if (responseCode == 0) {
      return results;
    }

    throw new Exception($"{canceledMessage} (code {responseCode}).");
  }

  private string GetToken() {
    int token = Interlocked.Increment(ref _requestCounter);
    return $"req_{token}";
  }

  /// <summary>
  /// Initialize the D-Bus connection and request the creation of a new Screencast session.
  /// </summary>
  /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
  /// <exception cref="InvalidOperationException">If a session is already running.</exception>
  public async Task CreateSessionAsync(CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(_isDisposed, this);
    if (_connection != null) throw new InvalidOperationException("Session already created.");

    string guid = Guid.NewGuid().ToString("N");
    string sessionToken = $"screencapture_session_{guid}";

    _connection = new Connection(Address.Session!);
    await _connection.ConnectAsync().ConfigureAwait(false);
    _screenCast = new OrgFreedesktopPortalScreenCastProxy(_connection, _destination, _path);
    _senderName = _connection.UniqueName!.Substring(1).Replace('.', '_');

    string sessionReqPath = $"{_path}/request/{_senderName}/{sessionToken}";

    var sessionOptions = new Dictionary<string, VariantValue> {
      { "handle_token", VariantValue.String(sessionToken) },
      { "session_handle_token", VariantValue.String(GetToken()) }
    };

    Dictionary<string, VariantValue> results = await RunRequestAndWaitAsync(
      sessionReqPath,
      () => _screenCast!.CreateSessionAsync(sessionOptions),
      "Session creation canceled",
      cancellationToken).ConfigureAwait(false);

    if (!results.TryGetValue("session_handle", out var sessionHandleValue)) {
      throw new Exception("Portal response did not include 'session_handle'.");
    }

    _sessionHandle = new(sessionHandleValue.GetString());
  }

  /// <summary>
  /// Set up the sources to capture, without displaying the UI to the user yet.
  /// </summary>
  /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
  /// <exception cref="InvalidOperationException">If CreateSessionAsync has not been called before.</exception>
  public async Task SelectSourcesAsync(CancellationToken cancellationToken = default) {
    ObjectDisposedException.ThrowIf(_isDisposed, this);
    if (_screenCast == null) throw new InvalidOperationException("You must await CreateSessionAsync() first.");

    string sourcesToken = GetToken();
    string sourcesReqPath = $"{_path}/request/{_senderName}/{sourcesToken}";

    var selectOptions = new Dictionary<string, VariantValue> {
      { "handle_token", VariantValue.String(sourcesToken) },
      { "types", VariantValue.UInt32((uint)ScreenCastSourceType.All) },
      { "multiple", VariantValue.Bool(false) },
      { "cursor_mode", VariantValue.UInt32((uint)CursorMode.Hidden) }
    };

    await RunRequestAndWaitAsync(
      sourcesReqPath,
      () => _screenCast!.SelectSourcesAsync(_sessionHandle, selectOptions),
      "Selection canceled",
      cancellationToken).ConfigureAwait(false);

    _sourcesSelected = true;
  }

  /// <summary>
  /// Display the XDG Desktop Portal ScreenCast UI to the user, and wait for his confirmation.
  /// Populate the property <see cref="StartResults"/> if the request has been accepted.
  /// </summary>
  /// <param name="cancellationToken">The token used to cancel the asynchronous operation.</param>
  /// <param name="parentWindow">An optional <a href="https://flatpak.github.io/xdg-desktop-portal/docs/window-identifiers.html">parent window identifier</a>.</param>
  /// <exception cref="InvalidOperationException">If SelectSourcesAsync has not been called before.</exception>
  public async Task StartAsync(CancellationToken cancellationToken = default, string parentWindow = "") {
    ObjectDisposedException.ThrowIf(_isDisposed, this);
    if (!_sourcesSelected) throw new InvalidOperationException("You must await SelectSourcesAsync() first.");

    string startToken = GetToken();
    string startReqPath = $"{_path}/request/{_senderName}/{startToken}";

    var startOptions = new Dictionary<string, VariantValue> {
      { "handle_token", VariantValue.String(startToken) }
    };

    _startResults = await RunRequestAndWaitAsync(
      startReqPath,
      () => _screenCast!.StartAsync(_sessionHandle, parentWindow, startOptions),
      "ScreenCast canceled",
      cancellationToken).ConfigureAwait(false);
  }

  /// <summary>
  /// Stop the Desktop Portal connection: dispose the session and clear memory.
  /// </summary>
  public async ValueTask DisposeAsync() {
    if (_isDisposed) return;
    _isDisposed = true;

    var connection = _connection;
    var sessionHandle = _sessionHandle;

    _sourcesSelected = false;
    _sessionHandle = default;
    _screenCast = null;
    _senderName = null;
    _startResults = null;
    _connection = null;

    if (connection != null) {
      if (sessionHandle != default) {
        try {
          var sessionProxy = new OrgFreedesktopPortalSessionProxy(connection, _destination, sessionHandle.ToString());
          await sessionProxy.CloseAsync().ConfigureAwait(false);
        }
        catch {}
      }
      connection.Dispose();
    }
  }
}
