import { api } from './client'
import type { Device, DeviceHealth, CreateCodeResponse } from './types'

export async function getDevices(signal?: AbortSignal): Promise<Device[]> {
  return api<Device[]>('/api/v1/devices', { signal })
}

export async function getDeviceHealth(signal?: AbortSignal): Promise<DeviceHealth[]> {
  return api<DeviceHealth[]>('/api/v1/devices/health', { signal })
}

export async function createEnrollmentCode(
  role?: 'device' | 'admin',
  signal?: AbortSignal
): Promise<CreateCodeResponse> {
  const body = role ? JSON.stringify({ role }) : JSON.stringify({})
  return api<CreateCodeResponse>('/api/v1/devices/codes', {
    method: 'POST',
    body,
    signal,
  })
}

export async function revokeDevice(deviceId: string, signal?: AbortSignal): Promise<void> {
  return api<void>(`/api/v1/devices/${encodeURIComponent(deviceId)}`, {
    method: 'DELETE',
    signal,
  })
}
