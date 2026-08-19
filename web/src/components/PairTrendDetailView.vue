<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { useRouter } from 'vue-router'
import { ReloadOutlined, StockOutlined, FullscreenOutlined } from '@ant-design/icons-vue'
import { marketApi } from '@/api/market'
import KlineChart from '@/components/KlineChart.vue'
import { formatTime, label, price } from '@/utils/format'
import type { PairChartMarker, PairTrendHit, PairTrendViewContext } from '@/types/market'

const props = defineProps<{ context: PairTrendViewContext; eventId: number; compact?: boolean }>()
const router = useRouter()
const detail = useQuery({
  queryKey: computed(() => ['pair-detail', props.context, props.eventId]),
  queryFn: () => marketApi.pairDetailForContext(props.context, props.eventId),
  refetchInterval: computed(() => props.context === 'intraday' ? 30_000 : false),
})
const event = computed(() => detail.data.value?.pairEvent)
const symbol = computed(() => event.value?.symbol ?? '')
const frequency = ref('5m')
const selectedHitId = ref<number>()

watch(() => [props.context, props.eventId, detail.data.value?.recommendedChart.frequency], () => {
  const recommended = detail.data.value?.recommendedChart.frequency
  if (recommended) frequency.value = recommended
}, { immediate: true })

const officialBarsEnabled = computed(() => Boolean(
  detail.data.value &&
  !detail.data.value.sourceInfo.isAcceptanceSample &&
  !symbol.value.startsWith('TEST.'),
))
const bars = useQuery({
  queryKey: computed(() => ['bars', 'pair-detail', symbol.value, frequency.value,
    detail.data.value?.recommendedChart.from, detail.data.value?.recommendedChart.to]),
  queryFn: () => marketApi.barsRange(
    symbol.value,
    frequency.value,
    detail.data.value!.recommendedChart.from,
    detail.data.value!.recommendedChart.to,
  ),
  enabled: officialBarsEnabled,
})
const currentHits = computed(() => (detail.data.value?.hits.items ?? [])
  .filter((hit) => hit.frequency === frequency.value))
const markers = computed<PairChartMarker[]>(() => currentHits.value.map((hit) => ({
  id: hit.id,
  time: hit.eob,
  price: hit.pairPrice,
  pivotType: hit.pivotType,
  status: hit.status,
  pairCode: hit.pairCode,
  pairKind: hit.pairKind,
  score: hit.score,
  selected: selectedHitId.value === hit.id,
})))
const availableFrequencies = computed(() => {
  const present = new Set((detail.data.value?.hits.items ?? []).map((hit) => hit.frequency))
  return ['5m', '30m', '60m', '1d'].map((value) => ({ label: value, value, disabled: !present.has(value) }))
})
const hitColumns = [
  { title: '周期', dataIndex: 'frequency', key: 'frequency', width: 70 },
  { title: 'K线结束', key: 'eob', width: 148 },
  { title: '对子', key: 'pair', width: 92 },
  { title: '字段', dataIndex: 'hitField', key: 'hitField', width: 72 },
  { title: '阶段', key: 'stage', width: 86 },
  { title: '作用', key: 'promotion', width: 86 },
  { title: '判定依据', dataIndex: 'confirmationReason', key: 'reason' },
]

function pairSuffix(hit: Pick<PairTrendHit, 'pairKind' | 'pairCode'>) {
  return hit.pairKind === 'ROUND_00' ? '.00' : `.${String(hit.pairCode).padStart(2, '0')}`
}
function openStock() {
  if (!event.value) return
  void router.push({ name: 'stock', params: { symbol: event.value.symbol }, query: { pairContext: props.context, pairEventId: props.eventId } })
}
function openFull() {
  void router.push({ name: props.context === 'intraday' ? 'pair-trend-intraday-detail' : 'pair-trend-history-data-detail', params: { id: props.eventId } })
}
</script>

<template>
  <div class="pair-detail-view">
    <a-skeleton v-if="detail.isLoading.value" active :paragraph="{ rows: 12 }" />
    <a-alert v-else-if="detail.isError.value" type="error" show-icon message="对子事件加载失败" description="记录可能已被清理，或服务暂时不可用。" />
    <template v-else-if="detail.data.value && event">
      <a-alert v-if="detail.data.value.sourceInfo.isAcceptanceSample" class="detail-alert" type="warning" show-icon message="算法验收样本" description="该记录用于验证对子算法，不代表真实 A 股行情，也不会请求官方 K 线。" />
      <header class="pair-detail-header">
        <div>
          <div class="detail-tag-line">
            <a-tag :class="event.pivotType === 'TOP' ? 'pair-top' : 'pair-bottom'">{{ event.pivotType === 'TOP' ? '阶段顶部' : '阶段底部' }}</a-tag>
            <a-tag>{{ props.context === 'intraday' ? '盘中实时' : '历史数据' }}</a-tag>
            <a-tag :color="event.stage === 'ESTABLISHED' ? 'red' : event.stage === 'FOCUS' ? 'purple' : event.stage === 'OBSERVING' ? 'orange' : 'default'">{{ label(event.stage) }}</a-tag>
            <a-tag>{{ event.isActive ? '当前有效' : '已失效' }}</a-tag>
            <span v-if="props.context === 'intraday'" class="realtime-indicator online">30 秒自动刷新</span>
          </div>
          <h2>{{ event.symbolName || event.symbol }} <small>{{ event.symbol }}</small></h2>
          <p>首次 {{ formatTime(event.firstSeenAt) }} · 最近 {{ formatTime(event.lastSeenAt) }}</p>
        </div>
        <div class="pair-price-block">
          <span>对子价格</span><strong>{{ price(event.latestPairPrice) }}</strong>
          <small>{{ event.latestPairKind === 'ROUND_00' ? '.00' : `.${String(event.latestPairCode).padStart(2,'0')}` }}</small>
        </div>
      </header>

      <div class="pair-detail-actions">
        <a-button @click="detail.refetch()"><ReloadOutlined />刷新</a-button>
        <a-button @click="openStock"><StockOutlined />查看个股</a-button>
        <a-button v-if="compact" type="primary" @click="openFull"><FullscreenOutlined />完整详情</a-button>
      </div>

      <div class="pair-metrics">
        <div><span>当前阶段</span><strong>{{ label(event.stage) }}</strong></div>
        <div><span>事件代次</span><strong>{{ event.generation }}</strong></div>
        <div><span>最强周期</span><strong>{{ event.strongestFrequency }}</strong></div>
        <div><span>命中总数</span><strong>{{ event.totalHitCount }}</strong></div>
        <div><span>升级证据</span><strong>{{ event.confirmedHitCount }}</strong></div>
        <div><span>价格 Tick</span><strong>{{ event.priceTicks ?? '—' }}</strong></div>
      </div>

      <div class="detail-section-head">
        <div><span class="eyebrow">OFFICIAL KLINE REVIEW</span><h3>官方 K 线复核</h3></div>
        <a-segmented v-model:value="frequency" :options="availableFrequencies" />
      </div>
      <div v-if="officialBarsEnabled" class="detail-chart-wrap">
        <KlineChart :bars="bars.data.value ?? []" :markers="markers" :loading="bars.isLoading.value" />
        <a-empty v-if="!bars.isLoading.value && (bars.data.value?.length ?? 0) === 0" description="当前窗口没有已固化的官方 K 线" />
      </div>
      <a-empty v-else description="验收样本不加载真实官方 K 线" />

      <a-tabs default-active-key="hits" class="pair-detail-tabs">
        <a-tab-pane key="hits" tab="命中明细">
          <a-table :columns="hitColumns" :data-source="detail.data.value.hits.items" row-key="id" size="small" :pagination="false" :scroll="{ x: 850 }" class="market-table pair-hit-table" :custom-row="(record: PairTrendHit) => ({ onClick: () => { selectedHitId = record.id; frequency = record.frequency } })">
            <template #bodyCell="{ column, record }">
              <template v-if="column.key === 'eob'"><span class="numeric muted">{{ formatTime(record.eob) }}</span></template>
              <template v-else-if="column.key === 'pair'"><strong class="numeric">{{ price(record.pairPrice) }}</strong> <small class="pair-code">{{ pairSuffix(record) }}</small></template>
              <template v-else-if="column.key === 'stage'"><span>{{ label(record.stage || record.status) }}</span></template>
              <template v-else-if="column.key === 'promotion'"><span>{{ record.isPromotion ? '阶段升级' : '5m证据' }}</span></template>
            </template>
          </a-table>
        </a-tab-pane>
        <a-tab-pane key="lifecycle" tab="状态时间线">
          <a-timeline>
            <a-timeline-item v-for="item in detail.data.value.lifecycles" :key="item.lifecycleKey" :color="item.toStage === 'INVALIDATED' ? 'red' : item.toStage === 'ESTABLISHED' ? 'red' : item.toStage === 'FOCUS' ? 'purple' : 'blue'">
              <strong>{{ item.fromStage ? `${label(item.fromStage)} → ` : '' }}{{ label(item.toStage) }}</strong>
              <span class="muted"> · {{ item.triggerFrequency }} · {{ price(item.triggerPrice) }} · {{ formatTime(item.occurredAt) }}</span>
              <div class="muted">{{ item.reason }}{{ item.shouldNotify ? ' · 已触发提醒' : '' }}</div>
            </a-timeline-item>
          </a-timeline>
        </a-tab-pane>
        <a-tab-pane key="audit" tab="判定与审计">
          <a-descriptions bordered size="small" :column="compact ? 1 : 2">
            <a-descriptions-item label="算法版本">{{ event.algorithmVersion }}</a-descriptions-item>
            <a-descriptions-item label="事件修订">{{ event.eventRevision ?? '历史记录无修订号' }}</a-descriptions-item>
            <a-descriptions-item label="运行模式">{{ detail.data.value.sourceInfo.runMode }}</a-descriptions-item>
            <a-descriptions-item label="数据来源">{{ detail.data.value.sourceInfo.dataSource }}</a-descriptions-item>
            <a-descriptions-item label="回放运行 ID">{{ detail.data.value.sourceInfo.runId ?? '—' }}</a-descriptions-item>
            <a-descriptions-item label="源事件 ID">{{ event.lastSourceEventId || '—' }}</a-descriptions-item>
            <a-descriptions-item label="确认时间">{{ formatTime(event.confirmedAt) }}</a-descriptions-item>
            <a-descriptions-item label="失效时间">{{ formatTime(event.invalidatedAt) }}</a-descriptions-item>
            <a-descriptions-item label="失效价格">{{ event.invalidatedPrice === undefined ? '—' : price(event.invalidatedPrice) }}</a-descriptions-item>
            <a-descriptions-item label="失效原因">{{ event.invalidationReason || '—' }}</a-descriptions-item>
            <a-descriptions-item label="事件键">{{ event.eventKey }}</a-descriptions-item>
            <a-descriptions-item label="备注" :span="2">{{ detail.data.value.sourceInfo.notes || '—' }}</a-descriptions-item>
          </a-descriptions>
        </a-tab-pane>
      </a-tabs>
    </template>
  </div>
</template>
