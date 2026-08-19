<script setup lang="ts">
import PairTrendDetailView from '@/components/PairTrendDetailView.vue'
import type { PairTrendViewContext } from '@/types/market'

defineProps<{ open: boolean; context: PairTrendViewContext; eventId: number }>()
defineEmits<{ close: [] }>()
</script>

<template>
  <a-drawer :open="open" width="min(960px, 94vw)" title="对子事件详情" :destroy-on-close="true" @close="$emit('close')">
    <PairTrendDetailView v-if="open && eventId > 0" :context="context" :event-id="eventId" compact />
  </a-drawer>
</template>
