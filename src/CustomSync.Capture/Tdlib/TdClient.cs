using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;

namespace CustomSync.Capture.Tdlib;

/// <summary>
/// Correlates outgoing TDLib requests with replies via @extra.
/// Runs a single dedicated thread for td_receive.
/// Uncorrelated incoming messages are published as updates.
/// </summary>
public class TdClient : ITdClient
{
    private record PendingRequest(TaskCompletionSource<string> Tcs);

    private readonly ITdTransport _transport;
    private readonly ILogger<TdClient>? _logger;
    private readonly TimeSpan _defaultTimeout;
    private readonly TimeSpan _errorBackoff;
    private readonly ConcurrentDictionary<string, PendingRequest> _pendingRequests = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Thread _receiveThread;
    private readonly int _clientId;
    private bool _disposed;

    public int ClientId => _clientId;
    public int PendingRequestCount => _pendingRequests.Count;
    public event Action<string>? UpdateReceived;

    public TdClient(
        ITdTransport transport,
        ILogger<TdClient>? logger = null,
        TimeSpan? defaultTimeout = null,
        TimeSpan? errorBackoff = null)
    {
        _transport = transport;
        _logger = logger;
        _defaultTimeout = defaultTimeout ?? TimeSpan.FromSeconds(30);
        _errorBackoff = errorBackoff ?? TimeSpan.FromSeconds(1);
        _clientId = _transport.CreateClientId();

        _receiveThread = new Thread(ReceiveLoop)
        {
            IsBackground = true,
            Name = $"TdClient-{_clientId}-Receive"
        };
        _receiveThread.Start();
    }

    public async Task<string> SendAsync(string requestJson, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var node = JsonNode.Parse(requestJson)?.AsObject()
            ?? throw new ArgumentException("Request must be a valid JSON object.", nameof(requestJson));

        string extra;
        if (node["@extra"] != null)
        {
            extra = node["@extra"]!.ToString();
        }
        else
        {
            extra = Guid.NewGuid().ToString("N");
            node["@extra"] = extra;
        }

        var payload = node.ToJsonString();
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest(tcs);

        _pendingRequests[extra] = pending;

        var effectiveTimeout = timeout ?? _defaultTimeout;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct, _stopCts.Token);
        cts.CancelAfter(effectiveTimeout);

        using (cts.Token.Register(() =>
        {
            if (_pendingRequests.TryRemove(extra, out var p))
            {
                if (_stopCts.IsCancellationRequested)
                {
                    p.Tcs.TrySetException(new ObjectDisposedException(nameof(TdClient), "Client was stopped or disposed."));
                }
                else if (ct.IsCancellationRequested)
                {
                    p.Tcs.TrySetCanceled(ct);
                }
                else
                {
                    p.Tcs.TrySetException(new TimeoutException($"TDLib request with @extra '{extra}' timed out after {effectiveTimeout.TotalSeconds} seconds."));
                }
            }
        }))
        {
            // Log HAR DOIM redaktor orqali: TDLib JSON ichida api_hash,
            // telefon raqami, kirish kodi va 2FA paroli ochiq turadi va
            // journal'da login'dan keyin ham qolib ketadi.
            _logger?.LogDebug("TDLib -> {Payload}", TdRedactor.Redact(payload));
            _transport.Send(_clientId, payload);
            return await tcs.Task;
        }
    }

    public string? Execute(string requestJson)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _transport.Execute(requestJson);
    }

    private void ReceiveLoop()
    {
        while (!_stopCts.IsCancellationRequested && !_disposed)
        {
            try
            {
                var raw = _transport.Receive(1.0);
                if (string.IsNullOrWhiteSpace(raw))
                {
                    continue;
                }

                _logger?.LogDebug("TDLib <- {Payload}", TdRedactor.Redact(raw));
                HandleIncoming(raw);
            }
            catch (Exception ex)
            {
                if (!_stopCts.IsCancellationRequested && !_disposed)
                {
                    _logger?.LogError(ex, "Error in TDLib receive loop");

                    // Kechikishsiz uzluksiz xato (masalan kutubxona uzilgan)
                    // 24/7 jarayonda protsessorni 100% band qiladi.
                    _stopCts.Token.WaitHandle.WaitOne(_errorBackoff);
                }
            }
        }
    }

    private void HandleIncoming(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        if (root.TryGetProperty("@extra", out var extraProp))
        {
            var extra = extraProp.ValueKind == JsonValueKind.String
                ? extraProp.GetString()
                : extraProp.GetRawText();

            if (!string.IsNullOrEmpty(extra) && _pendingRequests.TryRemove(extra, out var pending))
            {
                if (root.TryGetProperty("@type", out var typeProp) && typeProp.GetString() == "error")
                {
                    int code = root.TryGetProperty("code", out var codeProp) ? codeProp.GetInt32() : 500;
                    string msg = root.TryGetProperty("message", out var msgProp) ? msgProp.GetString() ?? "Unknown TDLib error" : "Unknown TDLib error";
                    pending.Tcs.TrySetException(new TdException(code, msg));
                }
                else
                {
                    pending.Tcs.TrySetResult(raw);
                }
                return;
            }
        }

        // Without matching @extra, it is an update
        try
        {
            UpdateReceived?.Invoke(raw);
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "Error in UpdateReceived event handler");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _stopCts.Cancel();

        // Join receive thread briefly if not called from receive thread itself
        if (_receiveThread.IsAlive && Thread.CurrentThread != _receiveThread)
        {
            _receiveThread.Join(TimeSpan.FromSeconds(2));
        }

        foreach (var kvp in _pendingRequests)
        {
            if (_pendingRequests.TryRemove(kvp.Key, out var p))
            {
                p.Tcs.TrySetException(new ObjectDisposedException(nameof(TdClient), "TdClient was disposed."));
            }
        }

        _transport.Dispose();
        _stopCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
