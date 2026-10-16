<script setup lang="ts">
defineProps<{
  title: string
  value?: string | number
  subtext?: string
  loading?: boolean
  error?: string | boolean | null
}>()
</script>

<template>
  <div class="cs-stat-card" :class="{ 'has-error': !!error, 'is-loading': loading }">
    <div class="cs-stat-title">{{ title }}</div>
    <div v-if="error" class="cs-stat-error">
      {{ typeof error === 'string' ? error : 'Xatolik yuz berdi' }}
    </div>
    <div v-else-if="loading" class="cs-stat-loading">
      Yuklanmoqda...
    </div>
    <div v-else class="cs-stat-value">
      {{ value ?? '-' }}
    </div>
    <div v-if="subtext && !error && !loading" class="cs-stat-subtext">
      {{ subtext }}
    </div>
  </div>
</template>

<style scoped>
.cs-stat-card {
  padding: 1rem;
  background: var(--surface-card, #ffffff);
  border: 1px solid var(--surface-border, #e5e7eb);
  border-radius: 6px;
  display: flex;
  flex-direction: column;
  gap: 0.25rem;
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
  line-height: 1.2;
}

.cs-stat-subtext {
  font-size: 0.75rem;
  color: var(--text-color-secondary, #6b7280);
}

.cs-stat-error {
  color: var(--red-500, #ef4444);
  font-size: 0.875rem;
  font-weight: 500;
}

.cs-stat-loading {
  font-size: 0.875rem;
  color: var(--text-color-secondary, #6b7280);
}
</style>
