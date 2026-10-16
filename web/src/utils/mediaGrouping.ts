import type { DecryptedIndexedRecord } from '../crypto/worker-api'

export interface DeviceMediaStatus {
  device_id: string
  status: string // 'present', 'pending', etc.
  reason?: string
  occurred_at: number
  seq?: number
}

export interface GroupedMediaFile {
  fileKey: string
  fileName: string
  relPath: string
  size: number
  sha256?: string
  devices: DeviceMediaStatus[]
  hasConflict: boolean
}

/**
 * media_index yozuvlarini fayl kaliti (sha256 ?? rel_path) bo'yicha guruhlaydi.
 * Har bir qurilma (device_id) uchun LWW qoidasi bo'yicha eng yangi (occurred_at eng katta)
 * holat saqlanadi. Agar fayl bir qurilmada 'present' va boshqasida 'pending' bo'lsa,
 * hasConflict = true bo'ladi.
 */
export function groupMediaIndexRecords(
  records: DecryptedIndexedRecord[]
): GroupedMediaFile[] {
  const groups = new Map<
    string,
    {
      fileKey: string
      fileName: string
      relPath: string
      size: number
      sha256?: string
      deviceMap: Map<string, DeviceMediaStatus>
    }
  >()

  for (const r of records) {
    if (r.kind !== 'media_index' || !r.json) continue

    const payload = r.json
    const sha256 = typeof payload.sha256 === 'string' && payload.sha256.trim() ? payload.sha256.trim() : undefined
    const relPath = typeof payload.rel_path === 'string' ? payload.rel_path : ''
    const fileKey = sha256 || relPath || r.record_id
    if (!fileKey) continue

    let group = groups.get(fileKey)
    if (!group) {
      group = {
        fileKey,
        fileName: payload.file_name || relPath || 'fayl',
        relPath,
        size: typeof payload.size === 'number' ? payload.size : 0,
        sha256,
        deviceMap: new Map(),
      }
      groups.set(fileKey, group)
    } else {
      if (!group.fileName && payload.file_name) group.fileName = payload.file_name
      if (!group.size && typeof payload.size === 'number') group.size = payload.size
      if (!group.sha256 && sha256) group.sha256 = sha256
    }

    const deviceId = r.device_id || 'unknown'
    const existing = group.deviceMap.get(deviceId)

    // LWW (Last-Write-Wins) by occurred_at (tie-breaker: seq)
    // Eski 'present' yangi 'pending' ustidan g'alaba qila olmaydi!
    const currentOcc = r.occurred_at
    const currentSeq = r.seq ?? 0

    if (!existing) {
      group.deviceMap.set(deviceId, {
        device_id: deviceId,
        status: payload.status || 'unknown',
        reason: payload.reason,
        occurred_at: currentOcc,
        seq: currentSeq,
      })
    } else {
      const isNewer =
        currentOcc > existing.occurred_at ||
        (currentOcc === existing.occurred_at && currentSeq > (existing.seq ?? 0))

      if (isNewer) {
        group.deviceMap.set(deviceId, {
          device_id: deviceId,
          status: payload.status || 'unknown',
          reason: payload.reason,
          occurred_at: currentOcc,
          seq: currentSeq,
        })
      }
    }
  }

  const result: GroupedMediaFile[] = []
  for (const group of groups.values()) {
    const devices = Array.from(group.deviceMap.values())
    const hasPresent = devices.some((d) => d.status === 'present')
    const hasPending = devices.some((d) => d.status === 'pending')
    const hasConflict = hasPresent && hasPending

    result.push({
      fileKey: group.fileKey,
      fileName: group.fileName,
      relPath: group.relPath,
      size: group.size,
      sha256: group.sha256,
      devices,
      hasConflict,
    })
  }

  return result
}
