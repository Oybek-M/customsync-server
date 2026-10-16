import { describe, it, expect } from 'vitest'
import { groupMediaIndexRecords } from '../utils/mediaGrouping'
import type { DecryptedIndexedRecord } from '../crypto/worker-api'

function createMediaRecord(options: {
  record_id: string
  device_id: string
  occurred_at: number
  seq?: number
  file_name?: string
  rel_path?: string
  sha256?: string
  size?: number
  status?: string
  reason?: string
}): DecryptedIndexedRecord {
  return {
    record_id: options.record_id,
    kind: 'media_index',
    peer_hash: 'peer-user-1',
    msg_id: options.seq ?? 1,
    occurred_at: options.occurred_at,
    seq: options.seq ?? 1,
    device_id: options.device_id,
    json: {
      kind: 'document',
      file_name: options.file_name ?? 'video.mp4',
      rel_path: options.rel_path ?? 'videos/video.mp4',
      size: options.size ?? 1048576,
      sha256: options.sha256,
      status: options.status ?? 'present',
      reason: options.reason,
    },
  }
}

describe('groupMediaIndexRecords', () => {
  it('LWW per device: older present does not beat newer pending', () => {
    // Record 1: present at occurred_at: 100
    const rec1 = createMediaRecord({
      record_id: 'rec-1',
      device_id: 'dev-laptop',
      occurred_at: 100,
      seq: 1,
      sha256: 'hash-abc-123',
      status: 'present',
    })

    // Record 2: pending (quota_full) at occurred_at: 200
    const rec2 = createMediaRecord({
      record_id: 'rec-2',
      device_id: 'dev-laptop',
      occurred_at: 200,
      seq: 2,
      sha256: 'hash-abc-123',
      status: 'pending',
      reason: 'quota_full',
    })

    const groups = groupMediaIndexRecords([rec1, rec2])
    expect(groups.length).toBe(1)
    expect(groups[0].devices.length).toBe(1)
    expect(groups[0].devices[0].status).toBe('pending')
    expect(groups[0].devices[0].reason).toBe('quota_full')
    expect(groups[0].devices[0].occurred_at).toBe(200)

    // Now test opposite arrival order: rec2 first, then older rec1
    const groupsReversed = groupMediaIndexRecords([rec2, rec1])
    expect(groupsReversed[0].devices[0].status).toBe('pending')
    expect(groupsReversed[0].devices[0].occurred_at).toBe(200)
  })

  it('groups by sha256 across devices even if rel_path differs', () => {
    const recA = createMediaRecord({
      record_id: 'rec-a',
      device_id: 'dev-desktop',
      occurred_at: 100,
      sha256: 'common-sha256-hash',
      file_name: 'desktop_name.mp4',
      rel_path: 'downloads/desktop_name.mp4',
      status: 'present',
    })

    const recB = createMediaRecord({
      record_id: 'rec-b',
      device_id: 'dev-phone',
      occurred_at: 120,
      sha256: 'common-sha256-hash',
      file_name: 'phone_name.mp4',
      rel_path: 'telegram/phone_name.mp4',
      status: 'pending',
      reason: 'quota_full',
    })

    const groups = groupMediaIndexRecords([recA, recB])
    expect(groups.length).toBe(1)
    expect(groups[0].fileKey).toBe('common-sha256-hash')
    expect(groups[0].devices.length).toBe(2)
  })

  it('falls back to rel_path grouping when sha256 is missing', () => {
    const rec1 = createMediaRecord({
      record_id: 'rec-no-sha-1',
      device_id: 'dev-1',
      occurred_at: 100,
      sha256: undefined,
      rel_path: 'shared/legacy_file.pdf',
      status: 'present',
    })

    const rec2 = createMediaRecord({
      record_id: 'rec-no-sha-2',
      device_id: 'dev-2',
      occurred_at: 150,
      sha256: undefined,
      rel_path: 'shared/legacy_file.pdf',
      status: 'present',
    })

    const groups = groupMediaIndexRecords([rec1, rec2])
    expect(groups.length).toBe(1)
    expect(groups[0].fileKey).toBe('shared/legacy_file.pdf')
    expect(groups[0].devices.length).toBe(2)
  })

  it('flags conflict when file is present on one device but pending on another', () => {
    // Group with conflict: present on dev-1, pending on dev-2
    const fileWithConflict = groupMediaIndexRecords([
      createMediaRecord({
        record_id: 'rec-c1',
        device_id: 'dev-1',
        occurred_at: 100,
        sha256: 'sha-conflict',
        status: 'present',
      }),
      createMediaRecord({
        record_id: 'rec-c2',
        device_id: 'dev-2',
        occurred_at: 110,
        sha256: 'sha-conflict',
        status: 'pending',
        reason: 'quota_full',
      }),
    ])
    expect(fileWithConflict[0].hasConflict).toBe(true)

    // Group without conflict: present on both devices
    const fileNoConflict = groupMediaIndexRecords([
      createMediaRecord({
        record_id: 'rec-nc1',
        device_id: 'dev-1',
        occurred_at: 100,
        sha256: 'sha-ok',
        status: 'present',
      }),
      createMediaRecord({
        record_id: 'rec-nc2',
        device_id: 'dev-2',
        occurred_at: 110,
        sha256: 'sha-ok',
        status: 'present',
      }),
    ])
    expect(fileNoConflict[0].hasConflict).toBe(false)
  })
})
