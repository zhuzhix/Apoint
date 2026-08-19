<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { ArrowLeftOutlined } from '@ant-design/icons-vue'
import PairTrendDetailView from '@/components/PairTrendDetailView.vue'
import type { PairTrendViewContext } from '@/types/market'

const route = useRoute()
const router = useRouter()
const context = computed<PairTrendViewContext>(() => route.meta.pairContext === 'history-data' ? 'history-data' : 'intraday')
const eventId = computed(() => Number(route.params.id))
</script>

<template>
  <section>
    <div class="detail-page-back"><a-button type="text" @click="router.back()"><ArrowLeftOutlined /> 返回</a-button></div>
    <PairTrendDetailView :context="context" :event-id="eventId" />
  </section>
</template>
