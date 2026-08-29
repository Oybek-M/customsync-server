using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace CustomSync.Api.Realtime;

/// <summary>
/// Jonli bildirishnoma. Ma'lumot bu kanal orqali YURMAYDI — yuboriladigan
/// yagona narsa "yangilik bor, pull qil" signali. Shu sababli SignalR emas,
/// oddiy WebSocket: payload trivial, va C++ hamda mobil klientlarda
/// QWebSocket / OkHttp / URLSession bilan qo'shimcha kutubxonasiz ishlaydi.
///
/// Bu kanal uzilsa tizim to'g'ri ishlashda davom etadi — periodik pull
/// asosiy yo'l bo'lib qoladi.
/// </summary>
public class NotifyHub
{
    // Qurilma soketlari xavfsiz concurrent to'plamda saqlanadi.
    // Ichki ConcurrentDictionary ishlatilishi tufayli Prune paytida yangi kelgan soketlar
    // tushib qolmaydi (poyga holati xavfsiz bartaraf etilgan).
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<WebSocket, byte>> _sockets = new();

    public void Register(string deviceId, WebSocket socket)
        => _sockets.GetOrAdd(deviceId, _ => new ConcurrentDictionary<WebSocket, byte>())[socket] = 0;

    public async Task NotifyOthersAsync(string originDeviceId, long seq)
    {
        var message = JsonSerializer.Serialize(new { type = "changes", seq });
        var bytes = Encoding.UTF8.GetBytes(message);

        foreach (var (deviceId, sockets) in _sockets)
        {
            if (deviceId == originDeviceId) continue;
            foreach (var socket in sockets.Keys)
            {
                if (socket.State != WebSocketState.Open) continue;
                try
                {
                    await socket.SendAsync(bytes, WebSocketMessageType.Text,
                        endOfMessage: true, CancellationToken.None);
                }
                catch (WebSocketException)
                {
                    // Uzilgan ulanish — e'tiborsiz qoldiramiz. Klient qayta
                    // ulanadi va oradagi o'zgarishlarni pull orqali oladi.
                }
            }
        }
    }

    public void Prune()
    {
        foreach (var (deviceId, sockets) in _sockets)
        {
            foreach (var socket in sockets.Keys)
            {
                if (socket.State != WebSocketState.Open)
                {
                    sockets.TryRemove(socket, out _);
                }
            }

            if (sockets.IsEmpty)
            {
                _sockets.TryRemove(new KeyValuePair<string, ConcurrentDictionary<WebSocket, byte>>(deviceId, sockets));
            }
        }
    }
}
