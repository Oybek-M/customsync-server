<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { useRouter } from 'vue-router'
import { useAuthStore } from '../stores/auth'
import { getDevices, revokeDevice, createEnrollmentCode } from '../api/devices'
import { getSettings } from '../api/settings'
import type { Device } from '../api/types'
import { formatDate } from '../utils/format'

const router = useRouter()
const authStore = useAuthStore()

const devices = ref<Device[]>([])
const loading = ref(false)
const error = ref<string | null>(null)

// Revoke dialog
const deviceToRevoke = ref<Device | null>(null)
const revoking = ref(false)

// New code modal
const showCodeModal = ref(false)
const selectedRole = ref<'device' | 'admin'>('device')
const generatedCode = ref<string | null>(null)
const codeRemainingSeconds = ref(0)
const codeExpired = ref(false)
const generatingCode = ref(false)
let countdownTimer: ReturnType<typeof setInterval> | null = null

let abortController: AbortController | null = null

async function loadData() {
  abortController = new AbortController()
  loading.value = true
  error.value = null

  try {
    devices.value = await getDevices(abortController.signal)
  } catch (err: unknown) {
    if (err instanceof DOMException && err.name === 'AbortError') return
    error.value = 'Qurilmalar ro\'yxatini yuklashda xatolik'
  } finally {
    loading.value = false
  }
}

onMounted(() => {
  loadData()
})

onUnmounted(() => {
  if (abortController) {
    abortController.abort()
  }
  if (countdownTimer) {
    clearInterval(countdownTimer)
  }
})

function openRevokeConfirm(device: Device) {
  deviceToRevoke.value = device
}

function closeRevokeConfirm() {
  deviceToRevoke.value = null
}

async function confirmRevoke() {
  if (!deviceToRevoke.value) return
  const target = deviceToRevoke.value
  revoking.value = true

  try {
    await revokeDevice(target.device_id)
    const isOwn = authStore.deviceId === target.device_id
    if (isOwn) {
      authStore.clearCredentials()
      await router.push('/enroll')
      return
    }

    // Update local state
    target.revoked_at = new Date().toISOString()
    closeRevokeConfirm()
  } catch {
    // Revoke error
  } finally {
    revoking.value = false
  }
}

async function openNewCodeModal() {
  showCodeModal.value = true
  generatedCode.value = null
  codeExpired.value = false
  codeRemainingSeconds.value = 0
  if (countdownTimer) clearInterval(countdownTimer)
}

function closeNewCodeModal() {
  showCodeModal.value = false
  if (countdownTimer) clearInterval(countdownTimer)
}

async function handleGenerateCode() {
  generatingCode.value = true
  codeExpired.value = false
  if (countdownTimer) clearInterval(countdownTimer)

  try {
    let ttlMinutes = 15
    try {
      const settings = await getSettings()
      const ttlSetting = settings.find((s) => s.key === 'auth.enroll_code_minutes')
      if (ttlSetting && !isNaN(parseInt(ttlSetting.value, 10))) {
        ttlMinutes = parseInt(ttlSetting.value, 10)
      }
    } catch {
      // Use default 15
    }

    const res = await createEnrollmentCode(selectedRole.value)
    generatedCode.value = res.code
    codeRemainingSeconds.value = ttlMinutes * 60

    countdownTimer = setInterval(() => {
      codeRemainingSeconds.value--
      if (codeRemainingSeconds.value <= 0) {
        codeExpired.value = true
        if (countdownTimer) clearInterval(countdownTimer)
      }
    }, 1000)
  } catch {
    // Error generating code
  } finally {
    generatingCode.value = false
  }
}

function formatCountdown(sec: number): string {
  if (sec <= 0) return '00:00'
  const m = Math.floor(sec / 60)
  const s = sec % 60
  return `${m.toString().padStart(2, '0')}:${s.toString().padStart(2, '0')}`
}
</script>

<template>
  <div class="cs-devices-view">
    <div class="cs-header-row">
      <div>
        <h2>Qurilmalar</h2>
        <p class="cs-subtitle">Ulangan mijoz qurilmalari ro'yxati va sinxronizatsiya holati.</p>
      </div>
      <div>
        <button class="cs-btn cs-btn-primary cs-btn-new-code" @click="openNewCodeModal">
          Yangi kod
        </button>
      </div>
    </div>

    <div v-if="error" class="cs-error-msg">
      {{ error }}
    </div>

    <div class="cs-table-wrap">
      <table class="cs-table">
        <thead>
          <tr>
            <th>Nom</th>
            <th>Platforma</th>
            <th>Rol</th>
            <th>Ulangan sana</th>
            <th>Oxirgi ko'rinish</th>
            <th>Cursor</th>
            <th>Holat</th>
            <th>Amallar</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="dev in devices" :key="dev.device_id">
            <td>
              <strong>{{ dev.name }}</strong>
            </td>
            <td>{{ dev.platform }}</td>
            <td>{{ dev.role }}</td>
            <td>{{ formatDate(dev.enrolled_at) }}</td>
            <td>{{ formatDate(dev.last_seen_at) }}</td>
            <td>{{ dev.last_cursor }}</td>
            <td>
              <span v-if="dev.revoked_at" class="cs-badge cs-badge-revoked">Bekor qilingan</span>
              <span v-else class="cs-badge cs-badge-active">Faol</span>
            </td>
            <td>
              <button
                v-if="!dev.revoked_at"
                class="cs-btn cs-btn-sm cs-btn-danger cs-btn-revoke"
                @click="openRevokeConfirm(dev)"
              >
                Bekor qilish
              </button>
            </td>
          </tr>
        </tbody>
      </table>
    </div>

    <!-- Confirm Dialog -->
    <div v-if="deviceToRevoke" class="cs-modal-backdrop">
      <div class="cs-modal cs-confirm-dialog">
        <h3>Qurilmani bekor qilish</h3>
        <p>
          Haqiqatan ham <strong>{{ deviceToRevoke.name }}</strong> qurilmasini bekor qilmoqchimisiz?
        </p>
        <div v-if="authStore.deviceId === deviceToRevoke.device_id" class="cs-own-device-warning">
          Diqqat: Bu joriy sessiyangiz ishlatayotgan qurilma! Bekor qilgandan so'ng qayta ulanishingiz kerak bo'ladi.
        </div>
        <div class="cs-modal-actions">
          <button class="cs-btn cs-btn-cancel" :disabled="revoking" @click="closeRevokeConfirm">
            Bekor qilish
          </button>
          <button class="cs-btn cs-btn-danger cs-btn-confirm" :disabled="revoking" @click="confirmRevoke">
            Tasdiqlash
          </button>
        </div>
      </div>
    </div>

    <!-- New Code Modal -->
    <div v-if="showCodeModal" class="cs-modal-backdrop">
      <div class="cs-modal cs-code-modal">
        <h3>Yangi ulanish kodi</h3>
        <div class="cs-form-group">
          <label for="role-select">Rol:</label>
          <select id="role-select" v-model="selectedRole" class="cs-select" :disabled="generatingCode">
            <option value="device">Qurilma (device)</option>
            <option value="admin">Admin (admin)</option>
          </select>
        </div>

        <div v-if="generatedCode" class="cs-code-result">
          <div class="cs-code-display" :class="{ 'is-expired': codeExpired }">
            {{ generatedCode }}
          </div>
          <div v-if="!codeExpired" class="cs-countdown">
            Qolgan vaqt: {{ formatCountdown(codeRemainingSeconds) }}
          </div>
          <div v-else class="cs-expired-msg is-expired">
            Muddati tugadi!
          </div>
        </div>

        <div class="cs-modal-actions">
          <button class="cs-btn cs-btn-cancel" @click="closeNewCodeModal">
            Yopish
          </button>
          <button
            class="cs-btn cs-btn-primary cs-btn-generate"
            :disabled="generatingCode"
            @click="handleGenerateCode"
          >
            Kod yaratish
          </button>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.cs-header-row {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1rem;
}

.cs-subtitle {
  color: var(--text-color-secondary, #6b7280);
  font-size: 0.875rem;
  margin-top: 0.25rem;
}

.cs-modal-backdrop {
  position: fixed;
  inset: 0;
  background: rgba(0, 0, 0, 0.4);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 1000;
}

.cs-modal {
  background: var(--surface-card, #ffffff);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 6px;
  padding: 1.25rem;
  width: 100%;
  max-width: 440px;
}

.cs-own-device-warning {
  background: #fef2f2;
  color: #b91c1c;
  padding: 0.75rem;
  border-radius: 4px;
  margin: 0.75rem 0;
  font-size: 0.8125rem;
}

.cs-modal-actions {
  display: flex;
  justify-content: flex-end;
  gap: 0.5rem;
  margin-top: 1.25rem;
}

.cs-code-result {
  margin: 1rem 0;
  text-align: center;
}

.cs-code-display {
  font-family: monospace;
  font-size: 1.5rem;
  font-weight: 700;
  padding: 0.75rem;
  background: var(--surface-ground, #f9fafb);
  border: 1px dashed var(--surface-border, #d1d5db);
  border-radius: 4px;
  letter-spacing: 0.1em;
}

.cs-code-display.is-expired {
  color: #9ca3af;
  text-decoration: line-through;
  background: #f3f4f6;
}

.cs-countdown {
  font-size: 0.875rem;
  color: var(--text-color-secondary, #6b7280);
  margin-top: 0.5rem;
}

.cs-expired-msg {
  font-size: 0.875rem;
  color: #ef4444;
  margin-top: 0.5rem;
  font-weight: 600;
}

.cs-form-group {
  margin-top: 0.75rem;
}

.cs-select {
  width: 100%;
  padding: 0.375rem 0.5rem;
  border: 1px solid var(--surface-border, #d1d5db);
  border-radius: 4px;
  margin-top: 0.25rem;
}
</style>
