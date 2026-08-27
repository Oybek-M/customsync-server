using System.Collections.Concurrent;

namespace CustomSync.Services;

public sealed class DeviceRevocationCache
{
    private readonly ConcurrentDictionary<string, byte> _revoked = new();

    public bool IsRevoked(string deviceId) => _revoked.ContainsKey(deviceId);
    public void Add(string deviceId)       => _revoked[deviceId] = 0;

    public void Load(IEnumerable<string> revokedDeviceIds)
    {
        foreach (var id in revokedDeviceIds) _revoked[id] = 0;
    }
}
