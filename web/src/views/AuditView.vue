<script setup lang="ts">
import { ref, onMounted, onUnmounted } from 'vue'
import { getAudit } from '../api/audit'
import type { AuditEvent } from '../api/types'
import { formatDate } from '../utils/format'

const events = ref<AuditEvent[]>([])
const nextBefore = ref<number | null>(null)
const loading = ref(false)
const loadingMore = ref(false)
const error = ref<string | null>(null)

let abortController: AbortController | null = null

async function loadInitial() {
  abortController = new AbortController()
  loading.value = true
  error.value = null

  try {
    const page = await getAudit(20, null, abortController.signal)
    events.value = page.items
    nextBefore.value = page.next_before
  } catch (err: unknown) {
    if (err instanceof DOMException && err.name === 'AbortError') return
    error.value = 'Audit yozuvlarini yuklashda xatolik yuz berdi'
  } finally {
    loading.value = false
  }
}

async function loadMore() {
  if (!nextBefore.value || loadingMore.value) return
  loadingMore.value = true

  try {
    const page = await getAudit(20, nextBefore.value, abortController?.signal)
    events.value = [...events.value, ...page.items]
    nextBefore.value = page.next_before
  } catch (err: unknown) {
    if (err instanceof DOMException && err.name === 'AbortError') return
    // Jim qolmaydi: aks holda jurnal shu yerda tugaydi deb o'ylanardi.
    error.value = "Keyingi sahifani yuklab bo'lmadi. Qayta urinib ko'ring."
  } finally {
    loadingMore.value = false
  }
}

onMounted(() => {
  loadInitial()
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
    // Plain text or unparseable string
  }
  return detail
}
</script>

<template>
  <div class="cs-audit-view">
    <div class="cs-header-row">
      <div>
        <h2>Audit</h2>
        <p class="cs-subtitle">Xavfsizlik voqealari va tizim harakatlari jurnali.</p>
      </div>
    </div>

    <div v-if="error" class="cs-error-msg">
      {{ error }}
    </div>

    <div class="cs-table-wrap">
      <table class="cs-table">
        <thead>
          <tr>
            <th>ID</th>
            <th>Vaqt</th>
            <th>Amal</th>
            <th>Qurilma</th>
            <th>Aktyor qurilma</th>
            <th>Tafsilot</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="ev in events" :key="ev.id">
            <td>#{{ ev.id }}</td>
            <td>{{ formatDate(ev.at) }}</td>
            <td>
              <code>{{ ev.action }}</code>
            </td>
            <td>{{ ev.device_id || '-' }}</td>
            <td>{{ ev.actor_device_id || '-' }}</td>
            <td class="cs-detail-cell">{{ formatDetail(ev.detail) }}</td>
          </tr>
        </tbody>
      </table>
    </div>

    <div v-if="nextBefore !== null" class="cs-load-more-wrap">
      <button
        class="cs-btn cs-btn-secondary cs-btn-load-more"
        :disabled="loadingMore"
        @click="loadMore"
      >
        {{ loadingMore ? 'Yuklanmoqda...' : 'Ko\'proq yuklash' }}
      </button>
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

.cs-detail-cell {
  max-width: 320px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: monospace;
  font-size: 0.8125rem;
}

.cs-load-more-wrap {
  display: flex;
  justify-content: center;
  margin-top: 1.5rem;
}
</style>
