<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { formatTime, label, price } from '@/utils/format'
import type { PairTrendTimelineEvent } from '@/types/market'

withDefaults(defineProps<{
  items: PairTrendTimelineEvent[]
  loading?: boolean
  showStock?: boolean
}>(), { loading: false, showStock: false })

defineEmits<{ open: [id: number] }>()

const isMobile = ref(false)
let mobileMediaQuery: MediaQueryList | undefined
function handleViewportChange(event: MediaQueryListEvent | MediaQueryList) { isMobile.value = event.matches }
onMounted(() => {
  mobileMediaQuery = window.matchMedia('(max-width: 720px)')
  handleViewportChange(mobileMediaQuery)
  mobileMediaQuery.addEventListener('change', handleViewportChange)
})
onBeforeUnmount(() => mobileMediaQuery?.removeEventListener('change', handleViewportChange))

function waveScore(record: PairTrendTimelineEvent) {
  if (record.pivotType !== 'BOTTOM' || record.waveCalculationStatus !== 'COMPLETED' || record.waveScore == null)
    return { text: '—', tone: 'muted', title: '暂无有效波段分数' }
  if (record.waveSignal === 'STRONG') return { text: String(record.waveScore), tone: 'strong', title: '强确认' }
  if (record.waveSignal === 'CANDIDATE') return { text: String(record.waveScore), tone: 'candidate', title: '候选' }
  return { text: String(record.waveScore), tone: 'muted', title: '未形成波段信号' }
}
function nextDayValidation(record: PairTrendTimelineEvent) {
  if (record.nextDayValidationStatus === 'MONITORING') return { text: '盘中验证中', tone: 'monitoring' }
  if (record.nextDayValidationStatus === 'INVALIDATED') return { text: '次日失效', tone: 'invalidated' }
  if (record.nextDayValidationStatus === 'PASSED') return { text: '次日通过', tone: 'passed' }
  if (record.nextDayValidationStatus === 'NO_TRADE') return { text: '次日停牌', tone: 'muted' }
  if (record.nextDayValidationStatus === 'NOT_APPLICABLE') return { text: '无需验证', tone: 'muted' }
  return { text: '—', tone: 'muted' }
}

const columns = [
  { title: '顶底日期', key: 'pivotAt', width: 156 },
  { title: '股票', key: 'stock', width: 150 },
  { title: '方向', key: 'pivot', width: 82 },
  { title: '对子价格', key: 'price', width: 104 },
  { title: '周期', dataIndex: 'frequencies', key: 'frequencies', width: 126 },
  { title: '截至结束日', key: 'stageAtEnd', width: 118 },
  { title: '当前状态', key: 'currentStage', width: 118 },
  { title: '波段分数', key: 'waveSignal', width: 100 },
  { title: '次日验证', key: 'nextDayValidation', width: 112 },
  { title: '失效时间', key: 'invalidatedAt', width: 156 },
  { title: '失效原因', key: 'invalidationReason', width: 180 },
  { title: '', key: 'actions', width: 68 },
]
</script>

<template>
  <a-table v-if="!isMobile"
    :columns="showStock ? columns : columns.filter((column) => column.key !== 'stock')"
    :data-source="items"
    :loading="loading"
    row-key="id"
    size="small"
    :pagination="false"
    :scroll="{ x: showStock ? 1390 : 1230 }"
    class="market-table pair-timeline-table pair-timeline-desktop"
  >
    <template #bodyCell="{ column, record }">
      <template v-if="column.key === 'pivotAt'">
        <span class="numeric">{{ formatTime(record.pivotAt) }}</span>
      </template>
      <template v-else-if="column.key === 'stock'">
        <div class="stock-cell"><strong>{{ record.symbolName || record.symbol }}</strong><span>{{ record.symbol }}</span></div>
      </template>
      <template v-else-if="column.key === 'pivot'">
        <a-tag :class="record.pivotType === 'TOP' ? 'pair-top' : 'pair-bottom'">{{ record.pivotType === 'TOP' ? '顶部' : '底部' }}</a-tag>
      </template>
      <template v-else-if="column.key === 'price'">
        <strong class="numeric" :class="record.pivotType === 'TOP' ? 'pair-price-top' : 'pair-price-bottom'">{{ price(record.pairPrice) }}</strong>
        <small v-if="record.pairKind === 'ROUND_00'" class="pair-code">.00</small>
      </template>
      <template v-else-if="column.key === 'stageAtEnd'">
        <span class="score-pill">{{ label(record.stageAtEnd) }}</span>
        <small class="state-validity">{{ record.isActiveAtEnd ? '有效' : '失效' }}</small>
      </template>
      <template v-else-if="column.key === 'currentStage'">
        <span>{{ label(record.currentStage) }}</span>
        <small class="state-validity">{{ record.currentIsActive ? '有效' : '失效' }}</small>
      </template>
      <template v-else-if="column.key === 'waveSignal'">
        <button v-if="waveScore(record).text !== '—'" type="button" class="wave-score-button wave-signal numeric" :class="`wave-${waveScore(record).tone}`" title="查看评分项" @click.stop="$emit('open', record.id)">{{ waveScore(record).text }}</button>
        <span v-else class="wave-signal numeric wave-muted">—</span>
      </template>
      <template v-else-if="column.key === 'nextDayValidation'">
        <span class="next-day-validation" :class="`validation-${nextDayValidation(record).tone}`" :title="record.nextDayBreachedAt ? `突破 ${formatTime(record.nextDayBreachedAt)} · ${price(record.nextDayBreachPrice ?? 0)}` : ''">{{ nextDayValidation(record).text }}</span>
      </template>
      <template v-else-if="column.key === 'invalidatedAt'">
        <span class="numeric muted">{{ record.invalidatedAt ? formatTime(record.invalidatedAt) : '—' }}</span>
      </template>
      <template v-else-if="column.key === 'invalidationReason'">
        <span class="invalidation-reason" :title="record.invalidationReason || '未失效'">{{ record.invalidationReason || '—' }}</span>
      </template>
      <template v-else-if="column.key === 'actions'">
        <a-button type="link" @click="$emit('open', record.id)">查看</a-button>
      </template>
    </template>
  </a-table>
  <div v-else class="pair-timeline-mobile">
    <article v-for="record in items" :key="record.id" class="pair-event-card">
      <header>
        <div v-if="showStock" class="stock-cell"><strong>{{ record.symbolName || record.symbol }}</strong><span>{{ record.symbol }}</span></div>
        <time class="numeric">{{ formatTime(record.pivotAt) }}</time>
        <a-tag :class="record.pivotType === 'TOP' ? 'pair-top' : 'pair-bottom'">{{ record.pivotType === 'TOP' ? '顶部' : '底部' }}</a-tag>
      </header>
      <div class="pair-event-card-main">
        <div><span>对子价格</span><strong class="numeric" :class="record.pivotType === 'TOP' ? 'pair-price-top' : 'pair-price-bottom'">{{ price(record.pairPrice) }}</strong></div>
        <div><span>周期</span><strong>{{ record.frequencies }}</strong></div>
        <div><span>截至结束日</span><strong>{{ label(record.stageAtEnd) }} · {{ record.isActiveAtEnd ? '有效' : '失效' }}</strong></div>
        <div><span>当前状态</span><strong>{{ label(record.currentStage) }} · {{ record.currentIsActive ? '有效' : '失效' }}</strong></div>
        <div><span>波段分数</span><button v-if="waveScore(record).text !== '—'" type="button" class="wave-score-button wave-signal numeric" :class="`wave-${waveScore(record).tone}`" title="查看评分项" @click.stop="$emit('open', record.id)">{{ waveScore(record).text }}</button><strong v-else class="wave-signal numeric wave-muted">—</strong></div>
        <div><span>次日验证</span><strong :class="`validation-${nextDayValidation(record).tone}`">{{ nextDayValidation(record).text }}</strong></div>
      </div>
      <div v-if="record.invalidatedAt || record.invalidationReason" class="pair-event-invalidated">
        <span>{{ record.invalidatedAt ? formatTime(record.invalidatedAt) : '已失效' }}</span>
        <p>{{ record.invalidationReason || '未记录失效原因' }}</p>
      </div>
      <footer><a-button type="link" @click="$emit('open', record.id)">查看详情</a-button></footer>
    </article>
    <div v-if="loading" class="mobile-loading">正在加载…</div>
  </div>
</template>

<style scoped>
.pair-price-top { color: #73d13d; }
.pair-price-bottom { color: #ff7875; }
.state-validity { display: block; margin-top: 4px; color: #73849b; font-size: 10px; }
.invalidation-reason { display:block; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; color:#8b9ab0; }
.wave-signal { display:inline-flex; align-items:center; min-height:22px; padding:2px 8px; border-radius:999px; font-size:11px; white-space:nowrap; }
.wave-score-button { border:0; font:inherit; cursor:pointer; }
.wave-score-button:hover,.wave-score-button:focus-visible { outline:1px solid currentColor; outline-offset:2px; }
.wave-strong { color:#ffec8b; background:rgba(250,173,20,.18); }
.wave-candidate { color:#91d5ff; background:rgba(24,144,255,.16); }
.wave-muted { color:#8291a6; background:rgba(130,145,166,.10); }
.next-day-validation { display:inline-flex; padding:2px 8px; border-radius:999px; font-size:11px; white-space:nowrap; }
.validation-passed { color:#95de64; background:rgba(82,196,26,.12); }
.validation-invalidated { color:#ff9c9c; background:rgba(255,77,79,.14); }
.validation-monitoring { color:#91caff; background:rgba(22,119,255,.14); }
.validation-muted { color:#8291a6; background:rgba(130,145,166,.10); }
@media (max-width:720px) {
  .pair-timeline-mobile { display:grid; gap:10px; }
  .pair-event-card { padding:14px; border:1px solid #243248; border-radius:10px; background:#111a2b; }
  .pair-event-card header { display:flex; align-items:center; gap:8px; }.pair-event-card header .stock-cell { flex:1; }.pair-event-card header time { flex:1; color:#cdd6e2; }
  .pair-event-card-main { display:grid; grid-template-columns:1fr 1fr; gap:8px; margin-top:12px; }
  .pair-event-card-main div { padding:8px; border-radius:7px; background:#0d1626; }.pair-event-card-main span,.pair-event-card-main strong { display:block; }
  .pair-event-card-main span { color:#71829a; font-size:10px; }.pair-event-card-main strong { margin-top:3px; color:#dce4ef; font-size:12px; }
  .pair-event-invalidated { margin-top:10px; padding:8px 10px; color:#9aa8ba; border-left:2px solid #ff7875; background:#0d1626; }
  .pair-event-invalidated span { font-size:10px; }.pair-event-invalidated p { margin:3px 0 0; font-size:11px; }
  .pair-event-card footer { margin-top:6px; text-align:right; }.mobile-loading { padding:20px; color:#8191a8; text-align:center; }
}
</style>
