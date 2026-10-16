<script lang="ts">
export function formatPreview(json: any, maxLen = 120): string {
  if (json === null || json === undefined) return '-'
  let str: string
  if (typeof json === 'string') {
    str = json
  } else if (typeof json === 'object') {
    if (json.text !== undefined) {
      str = typeof json.text === 'string' ? json.text : JSON.stringify(json.text)
    } else {
      try {
        str = JSON.stringify(json)
      } catch {
        str = String(json)
      }
    }
  } else {
    str = String(json)
  }
  if (str.length > maxLen) {
    return str.slice(0, maxLen) + '…'
  }
  return str
}
</script>

<script setup lang="ts">
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import type { DecryptedIndexedRecord } from '../crypto/worker-api'
import { formatDate } from '../utils/format'

export interface RecordTableProps {
  rows: DecryptedIndexedRecord[]
  loading?: boolean
  deviceNames?: Record<string, string>
}

const props = withDefaults(defineProps<RecordTableProps>(), {
  loading: false,
  deviceNames: () => ({}),
})

const emit = defineEmits<{
  (e: 'lazyLoad', event?: any): void
}>()

function handleLazyLoad(e?: any) {
  emit('lazyLoad', e)
}

function formatDevice(deviceId?: string): string {
  if (!deviceId) return '-'
  if (props.deviceNames && props.deviceNames[deviceId]) {
    return props.deviceNames[deviceId]
  }
  return deviceId.slice(0, 8)
}

function formatPeer(peerHash?: string): string {
  if (!peerHash) return '-'
  return peerHash.slice(0, 8) + '…'
}
</script>

<template>
  <div class="cs-record-table-container">
    <DataTable
      :value="rows"
      scrollable
      scrollHeight="flex"
      :virtualScrollerOptions="{
        lazy: true,
        itemSize: 34,
        onLazyLoad: handleLazyLoad,
        showLoader: true,
        delay: 0,
      }"
      dataKey="record_id"
      class="cs-record-table"
      :loading="loading"
    >
      <template #empty>
        <div class="cs-empty-table">
          {{ loading ? 'Yuklanmoqda...' : 'Yozuvlar topilmadi' }}
        </div>
      </template>

      <!-- ID / Seq ustuni -->
      <Column field="record_id" header="ID / Seq" :style="{ width: '180px' }">
        <template #body="{ data }">
          <code class="cs-record-id">{{ data.record_id }}</code>
          <span class="cs-seq-badge">#{{ data.seq }}</span>
        </template>
      </Column>

      <!-- Vaqt (local, occurred_at) -->
      <Column field="occurred_at" header="Vaqt" :style="{ width: '160px' }">
        <template #body="{ data }">
          <span class="cs-time">{{ formatDate(data.occurred_at) }}</span>
        </template>
      </Column>

      <!-- Turi -->
      <Column field="kind" header="Turi" :style="{ width: '110px' }">
        <template #body="{ data }">
          <span class="cs-badge" :class="`cs-badge-${data.kind}`">{{ data.kind }}</span>
        </template>
      </Column>

      <!-- Muxbir (peer_hash birinchi 8 belgisi) -->
      <Column field="peer_hash" header="Muxbir" :style="{ width: '120px' }">
        <template #body="{ data }">
          <code class="cs-peer-hash">{{ formatPeer(data.peer_hash) }}</code>
        </template>
      </Column>

      <!-- Qurilma nomi -->
      <Column field="device_id" header="Qurilma" :style="{ width: '140px' }">
        <template #body="{ data }">
          <span class="cs-device-name" :title="data.device_id">{{ formatDevice(data.device_id) }}</span>
        </template>
      </Column>

      <!-- Qisqa preview (text, xavfsiz) -->
      <Column field="json" header="Mazmun">
        <template #body="{ data }">
          <span class="cs-json-preview">{{ formatPreview(data.json) }}</span>
        </template>
      </Column>
    </DataTable>
  </div>
</template>

<style scoped>
.cs-record-table-container {
  height: 600px;
  display: flex;
  flex-direction: column;
}

:deep(.p-datatable-table) {
  font-size: 0.8125rem;
}

:deep(.p-datatable-tbody > tr) {
  height: 34px;
}

.cs-record-id {
  font-family: monospace;
  font-size: 0.75rem;
}

.cs-seq-badge {
  margin-left: 0.375rem;
  font-size: 0.75rem;
  color: var(--text-color-secondary, #6b7280);
}

.cs-time {
  font-size: 0.75rem;
}

.cs-badge {
  display: inline-block;
  padding: 0.125rem 0.375rem;
  border-radius: 3px;
  background: var(--surface-ground, #f3f4f6);
  font-size: 0.75rem;
  font-family: monospace;
}

.cs-peer-hash {
  font-size: 0.75rem;
}

.cs-device-name {
  font-size: 0.8125rem;
}

.cs-json-preview {
  display: inline-block;
  max-width: 500px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.cs-empty-table {
  padding: 2rem;
  text-align: center;
  color: var(--text-color-secondary, #6b7280);
}
</style>
