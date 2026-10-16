<script setup lang="ts">
import { ref, computed, onMounted, onUnmounted } from 'vue'
import StatCard from '../components/StatCard.vue'
import { getStorageStats } from '../api/stats'
import { getDevices } from '../api/devices'
import { getAudit } from '../api/audit'
import type { StorageStats, Device, AuditEvent } from '../api/types'
import { formatBytes, formatNumber, formatDate } from '../utils/format'

const storageStats = ref<StorageStats | null>(null)
const devices = ref<Device[]>([])
const recentAudit = ref<AuditEvent[]>([])

const storageLoading = ref(false)
const devicesLoading = ref(false)
const auditLoading = ref(false)

const storageError = ref<string | null>(null)
const devicesError = ref<string | null>(null)
const auditError = ref<string | null>(null)

let abortController: AbortController | null = null

const activeDevicesCount = computed(() => {
  return devices.value.filter((d) => !d.revoked_at).length
})

const lastSync = computed(() => {
  const dates = devices.value
    .map((d) => d.last_seen_at)
    .filter(Boolean) as string[]
  if (!dates.length) return '-'
  dates.sort().reverse()
  return formatDate(dates[0])
})

function loadData() {
  abortController = new AbortController()
  const signal = abortController.signal

  storageLoading.value = true
  devicesLoading.value = true
  auditLoading.value = true

  storageError.value = null
  devicesError.value = null
  auditError.value = null

  // 1. Storage stats
  getStorageStats(signal)
    .then((res) => {
      storageStats.value = res
    })
    .catch((err) => {
      if (err instanceof DOMException && err.name === 'AbortError') return
      storageError.value = 'Xatolik'
    })
    .finally(() => {
      storageLoading.value = false
    })

  // 2. Devices
  getDevices(signal)
    .then((res) => {
      devices.value = res
    })
    .catch((err) => {
      if (err instanceof DOMException && err.name === 'AbortError') return
      devicesError.value = 'Xatolik'
    })
    .finally(() => {
      devicesLoading.value = false
    })

  // 3. Audit
  getAudit(10, null, signal)
    .then((res) => {
      recentAudit.value = res.items
    })
    .catch((err) => {
      if (err instanceof DOMException && err.name === 'AbortError') return
      auditError.value = 'Xatolik'
    })
    .finally(() => {
      auditLoading.value = false
    })
}

onMounted(() => {
  loadData()
})

onUnmounted(() => {
  if (abortController) {
    abortController.abort()
  }
})

function formatDetail(detail: string | null): string {
  if (!detail) return '-'
  try {
    const parsed = JSON.parse(detail)
    if (typeof parsed === 'object' && parsed !== null) {
      return Object.entries(parsed)
        .map(([k, v]) => `${k}: ${v}`)
        .join(', ')
    }
  } catch {
    // fallback plain text
  }
  return detail
}
</script>

<template>
  <div class="cs-dashboard-view">
    <div class="cs-header-row">
      <div>
        <h2>Boshqaruv paneli</h2>
        <p class="cs-subtitle">Server holati va asosiy ko'rsatkichlar.</p>
      </div>
    </div>

    <!-- 4 StatCards -->
    <div class="cs-stats-grid">
      <StatCard
        title="Jami yozuvlar"
        :value="formatNumber(storageStats?.record_count)"
        :subtext="storageStats ? formatBytes(storageStats.record_bytes) + ' yozuvlar hajmi' : undefined"
        :loading="storageLoading"
        :error="storageError"
      />
      <StatCard
        title="Disk hajmi"
        :value="formatBytes((storageStats?.record_bytes ?? 0) + (storageStats?.media_bytes ?? 0))"
        :subtext="storageStats ? formatBytes(storageStats.media_bytes) + ' media' : undefined"
        :loading="storageLoading"
        :error="storageError"
      />
      <StatCard
        title="Faol qurilmalar"
        :value="activeDevicesCount"
        :subtext="devices.length ? `${devices.length} ta jami` : undefined"
        :loading="devicesLoading"
        :error="devicesError"
      />
      <StatCard
        title="Oxirgi sinxronizatsiya"
        :value="lastSync"
        :loading="devicesLoading"
        :error="devicesError"
      />
    </div>

    <div class="cs-dashboard-grid">
      <!-- Devices Overview -->
      <div class="cs-section">
        <div class="cs-section-header">
          <h3>Qurilmalar holati</h3>
          <router-link to="/devices" class="cs-link">Barchasi &rarr;</router-link>
        </div>
        <div v-if="devicesError" class="cs-error-msg">{{ devicesError }}</div>
        <div v-else class="cs-table-wrap">
          <table class="cs-table">
            <thead>
              <tr>
                <th>Nom</th>
                <th>Platforma</th>
                <th>Oxirgi ko'rinish</th>
                <th>Holat</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="dev in devices" :key="dev.device_id">
                <td><strong>{{ dev.name }}</strong></td>
                <td>{{ dev.platform }}</td>
                <td>{{ formatDate(dev.last_seen_at) }}</td>
                <td>
                  <span v-if="dev.revoked_at" class="cs-badge cs-badge-revoked">Bekor qilingan</span>
                  <span v-else class="cs-badge cs-badge-active">Faol</span>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- Recent Audit -->
      <div class="cs-section">
        <div class="cs-section-header">
          <h3>So'nggi audit hodisalari</h3>
          <router-link to="/audit" class="cs-link">Barchasi &rarr;</router-link>
        </div>
        <div v-if="auditError" class="cs-error-msg">{{ auditError }}</div>
        <div v-else class="cs-table-wrap">
          <table class="cs-table">
            <thead>
              <tr>
                <th>Vaqt</th>
                <th>Amal</th>
                <th>Tafsilot</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="ev in recentAudit" :key="ev.id">
                <td>{{ formatDate(ev.at) }}</td>
                <td><code>{{ ev.action }}</code></td>
                <td class="cs-detail-cell">{{ formatDetail(ev.detail) }}</td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.cs-header-row {
  margin-bottom: 1.5rem;
}

.cs-subtitle {
  color: var(--text-color-secondary, #6b7280);
  font-size: 0.875rem;
  margin-top: 0.25rem;
}

.cs-stats-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 1rem;
  margin-bottom: 2rem;
}

.cs-dashboard-grid {
  display: grid;
  grid-template-columns: 1fr;
  gap: 2rem;
}

@media (min-width: 1024px) {
  .cs-dashboard-grid {
    grid-template-columns: 1fr 1fr;
  }
}

.cs-section {
  background: var(--surface-card, #ffffff);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 6px;
  padding: 1.25rem;
}

.cs-section-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 1rem;
}

.cs-section-header h3 {
  font-size: 1rem;
  margin: 0;
}

.cs-link {
  font-size: 0.8125rem;
  color: var(--primary-color, #3b82f6);
  text-decoration: none;
}

.cs-link:hover {
  text-decoration: underline;
}

.cs-detail-cell {
  max-width: 250px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: monospace;
  font-size: 0.75rem;
}
</style>
