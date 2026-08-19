<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { useQuery } from '@tanstack/vue-query'
import { ArrowLeftOutlined } from '@ant-design/icons-vue'
import { marketApi } from '@/api/market'
import TaskCard from '@/components/TaskCard.vue'
import KlineChart from '@/components/KlineChart.vue'
import type { NotificationTask, PairChartMarker, PairTrendViewContext } from '@/types/market'

const route = useRoute()
const router = useRouter()
const symbol = computed(() => String(route.params.symbol))
const pairEventId = computed(() => Number(route.query.pairEventId ?? 0))
const pairContext = computed<PairTrendViewContext>(() => route.query.pairContext === 'history-data' ? 'history-data' : 'intraday')
const pairDetail = useQuery({
  queryKey: computed(() => ['pair-detail', pairContext.value, pairEventId.value]),
  queryFn: () => marketApi.pairDetailForContext(pairContext.value, pairEventId.value),
  enabled: computed(() => pairEventId.value > 0),
  refetchInterval: computed(() => pairEventId.value > 0 && pairContext.value === 'intraday' ? 30_000 : false),
})
const frequency = ref('5m')
const days = computed(() => frequency.value === '1d' ? 180 : 30)
watch(() => pairDetail.data.value?.recommendedChart.frequency, (value) => { if (value) frequency.value = value }, { immediate: true })
const bars = useQuery({
  queryKey: computed(() => ['bars', symbol.value, frequency.value, pairDetail.data.value?.recommendedChart.from, pairDetail.data.value?.recommendedChart.to]),
  queryFn: () => pairDetail.data.value
    ? marketApi.barsRange(symbol.value, frequency.value, pairDetail.data.value.recommendedChart.from, pairDetail.data.value.recommendedChart.to)
    : marketApi.bars(symbol.value, frequency.value, days.value),
  enabled: computed(() => !pairDetail.data.value?.sourceInfo.isAcceptanceSample),
})
const tasks = useQuery({ queryKey: computed(() => ['notifications', 'stock', symbol.value]), queryFn: () => marketApi.notifications({ symbol: symbol.value, pageSize: 50, userStatus: 'active' }) })
const latest = computed(() => bars.data.value?.at(-1))
const markers = computed<PairChartMarker[]>(() => (pairDetail.data.value?.hits.items ?? [])
  .filter((hit) => hit.frequency === frequency.value)
  .map((hit) => ({ id: hit.id, time: hit.eob, price: hit.pairPrice, pivotType: hit.pivotType, status: hit.status, pairCode: hit.pairCode, pairKind: hit.pairKind, score: hit.score })))
function open(task: NotificationTask) { void task }
</script>

<template>
  <section>
    <a-alert v-if="pairDetail.data.value" class="pair-context-alert" :type="pairDetail.data.value.sourceInfo.isAcceptanceSample ? 'warning' : 'info'" show-icon>
      <template #message>当前正在复核对子{{ pairDetail.data.value.pairEvent.pivotType === 'TOP' ? '顶部' : '底部' }}事件</template>
      <template #description>
        {{ pairDetail.data.value.pairEvent.strongestFrequency }} · 对子价 {{ pairDetail.data.value.pairEvent.latestPairPrice.toFixed(2) }} · {{ pairContext === 'intraday' ? '盘中实时' : '历史数据' }}
        <a-button type="link" size="small" @click="router.push({name:pairContext === 'intraday' ? 'pair-trend-intraday-detail' : 'pair-trend-history-data-detail',params:{id:pairEventId}})">返回事件详情</a-button>
      </template>
    </a-alert>
    <div class="stock-header">
      <a-button type="text" @click="router.back()"><ArrowLeftOutlined /></a-button>
      <div><span class="eyebrow">STOCK RESEARCH</span><h2>{{ tasks.data.value?.items[0]?.symbolName || symbol }}</h2><p>{{ symbol }}</p></div>
      <div class="latest-price" v-if="latest"><span>最新收盘</span><strong>{{ latest.closePrice.toFixed(2) }}</strong><small>{{ latest.officialConfirmed ? '东方掘金官方' : '盘中预览' }}</small></div>
    </div>
    <div class="chart-panel">
      <div class="panel-toolbar"><h3>官方 K 线</h3><a-segmented v-model:value="frequency" :options="['5m','30m','60m','1d']" /></div>
      <KlineChart :bars="bars.data.value ?? []" :markers="markers" :loading="bars.isLoading.value" />
    </div>
    <div class="section-heading compact"><div><span class="eyebrow">EVENT TIMELINE</span><h2>相关任务</h2></div></div>
    <div class="task-grid"><TaskCard v-for="task in tasks.data.value?.items ?? []" :key="task.taskKey" :task="task" @open="open" /></div>
  </section>
</template>
