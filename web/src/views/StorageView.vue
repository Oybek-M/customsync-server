<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { getStorageStats, getPeerStats } from '../api/stats'
import { getDeviceHealth } from '../api/devices'
import type { StorageStats, PeerStat, DeviceHealth } from '../api/types'
import { formatBytes, formatNumber, formatDate, shortenHash } from '../utils/format'

const storageStats = ref<StorageStats | null>(null)
const peers = ref<PeerStat[]>([])
const healthList = ref<DeviceHealth[]>([])
const loading = ref(false)
const error = ref<string | null>(null)

let abortController: AbortController | null = null

async function loadData() {
  abortController = new AbortController()
  loading.value = true
  error.value = null

  try {
    const [sData, pData, hData] = await Promise.all([
      getStorageStats(abortController.signal),
      getPeerStats('bytes', 10, abortController.signal),
      getDeviceHealth(abortController.signal),
    ])
    storageStats.value = sData
    peers.value = pData
    healthList.value = hData
  } catch (err: unknown) {
    if (err instanceof DOMException && err.name === 'AbortError') return
    error.value = 'Xotira ma\'lumotlarini yuklashda xatolik yuz berdi'
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
})
</script>

<template>
  <div class="cs-storage-view">
    <div class="cs-header-row">
      <div>
        <h2>Xotira</h2>
        <p class="cs-subtitle">Disk maydoni, kvotalar va tozalash mexanizmlari.</p>
      </div>
    </div>

    <div v-if="error" class="cs-error-msg">
      {{ error }}
    </div>

    <!-- Storage Totals -->
    <div class="cs-stats-grid">
      <div class="cs-stat-card">
        <div class="cs-stat-title">Jami yozuvlar</div>
        <div class="cs-stat-value">{{ formatNumber(storageStats?.record_count) }}</div>
        <div class="cs-stat-subtext">{{ formatBytes(storageStats?.record_bytes) }}</div>
      </div>
      <div class="cs-stat-card">
        <div class="cs-stat-title">Media fayllar</div>
        <div class="cs-stat-value">{{ formatNumber(storageStats?.media_count) }}</div>
        <div class="cs-stat-subtext">{{ formatBytes(storageStats?.media_bytes) }}</div>
      </div>
      <div class="cs-stat-card">
        <div class="cs-stat-title">Umumiy hajm</div>
        <div class="cs-stat-value">
          {{ formatBytes((storageStats?.record_bytes ?? 0) + (storageStats?.media_bytes ?? 0)) }}
        </div>
      </div>
    </div>

    <!-- Top Peers -->
    <div class="cs-section">
      <h3>Eng faol suhbatlar (Top 10 peer)</h3>
      <div class="cs-table-wrap">
        <table class="cs-table">
          <thead>
            <tr>
              <th>Peer Hash</th>
              <th>Yozuvlar soni</th>
              <th>Hajmi</th>
              <th>Oxirgi faollik</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="peer in peers" :key="peer.peer_hash">
              <td>
                <code>{{ shortenHash(peer.peer_hash) }}</code>
              </td>
              <td>{{ formatNumber(peer.record_count) }}</td>
              <td>{{ formatBytes(peer.total_bytes) }}</td>
              <td>{{ formatDate(peer.last_occurred_at) }}</td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- Device Health -->
    <div class="cs-section">
      <h3>Qurilmalar xotirasi va holati</h3>
      <div class="cs-table-wrap">
        <table class="cs-table">
          <thead>
            <tr>
              <th>Qurilma</th>
              <th>Platforma</th>
              <th>RSS (Xotira)</th>
              <th>Media fayllar</th>
              <th>Bo'sh disk</th>
              <th>Oxirgi hisobot</th>
              <th>Holat</th>
            </tr>
          </thead>
          <tbody>
            <tr
              v-for="dev in healthList"
              :key="dev.device_id"
              :class="{ 'is-stale': dev.stale }"
            >
              <td>
                <strong>{{ dev.name }}</strong>
              </td>
              <td>{{ dev.platform }}</td>
              <td>{{ formatBytes(dev.rss_bytes) }}</td>
              <td>{{ formatBytes(dev.media_store_bytes) }} ({{ dev.media_store_files }} ta)</td>
              <td>{{ formatBytes(dev.free_disk_bytes) }}</td>
              <td>{{ formatDate(dev.reported_at) }}</td>
              <td>
                <span v-if="dev.stale" class="cs-badge cs-badge-stale is-stale">Eskirgan</span>
                <span v-else class="cs-badge cs-badge-active">Yangi</span>
              </td>
            </tr>
          </tbody>
        </table>
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
  grid-template-columns: repeat(auto-fit, minmax(200px, 1fr));
  gap: 1rem;
  margin-bottom: 2rem;
}

.cs-stat-card {
  padding: 1rem;
  background: var(--surface-card, #ffffff);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 6px;
}

.cs-stat-title {
  font-size: 0.8125rem;
  color: var(--text-color-secondary, #6b7280);
  font-weight: 500;
}

.cs-stat-value {
  font-size: 1.5rem;
  font-weight: 700;
  color: var(--text-color, #111827);
  margin-top: 0.25rem;
}

.cs-stat-subtext {
  font-size: 0.75rem;
  color: var(--text-color-secondary, #6b7280);
  margin-top: 0.125rem;
}

.cs-section {
  margin-bottom: 2rem;
}

.cs-section h3 {
  font-size: 1rem;
  margin-bottom: 0.75rem;
}

.cs-badge-stale {
  background: #fef3c7;
  color: #b45309;
}

tr.is-stale {
  opacity: 0.75;
}
</style>
