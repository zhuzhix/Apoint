<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useQuery } from '@tanstack/vue-query'
import { useRoute, useRouter } from 'vue-router'
import { LockOutlined, ReloadOutlined } from '@ant-design/icons-vue'
import { marketApi } from '@/api/market'
import EmptyState from '@/components/EmptyState.vue'
import PairTrendTimelineTable from '@/components/PairTrendTimelineTable.vue'
import { ensureGroupEventState, type GroupEventState } from '@/composables/groupEventState'
import { formatTime, label } from '@/utils/format'
import type { PairTrendStockGroup, PairTrendViewContext } from '@/types/market'

type HistoryView = 'groups' | 'events'

const route = useRoute()
const router = useRouter()
const PairTrendDetailDrawer = defineAsyncComponent(() => import('@/components/PairTrendDetailDrawer.vue'))
const pageVisible = ref(typeof document === 'undefined' || !document.hidden)
const isMobile = ref(false)
const expandedSymbols = ref<string[]>([])
const groupEvents = reactive<Record<string, GroupEventState>>({})
let mobileMediaQuery: MediaQueryList | undefined

function formatDateValue(value: Date) {
  const year = value.getFullYear()
  const month = String(value.getMonth() + 1).padStart(2, '0')
  const day = String(value.getDate()).padStart(2, '0')
  return `${year}-${month}-${day}`
}
function defaultDateRange(): [string, string] {
  const to = new Date()
  const from = new Date(to)
  from.setDate(from.getDate() - 59)
  return [formatDateValue(from), formatDateValue(to)]
}
function textQuery(value: unknown, fallback = '') { return typeof value === 'string' ? value : fallback }
function positiveInt(value: unknown, fallback: number) {
  const parsed = Number(value)
  return Number.isInteger(parsed) && parsed > 0 ? parsed : fallback
}

const context = computed<PairTrendViewContext>(() => route.meta.pairContext === 'history-data' ? 'history-data' : 'intraday')
const isIntraday = computed(() => context.value === 'intraday')
const historyView = computed<HistoryView>(() => route.query.view === 'events' ? 'events' : 'groups')
const currentPage = computed(() => positiveInt(route.query.page, 1))
const eventId = computed(() => positiveInt(route.query.eventId, 0))
const initialRange = defaultDateRange()
const filterForm = reactive({
  keyword: textQuery(route.query.keyword), pivotType: textQuery(route.query.pivotType),
  stageAtEnd: textQuery(route.query.stageAtEnd), frequency: textQuery(route.query.frequency),
  activeAtEnd: textQuery(route.query.activeAtEnd),
})
const dateRange = ref<[string, string]>([
  textQuery(route.query.dateFrom, initialRange[0]), textQuery(route.query.dateTo, initialRange[1]),
])

const capabilities = useQuery({ queryKey: ['pair-trend-capabilities'], queryFn: marketApi.pairCapabilities, staleTime: 300_000 })
const intradayStatus = useQuery({
  queryKey: ['pair-trend-intraday-status'], queryFn: marketApi.pairIntradayStatus, enabled: isIntraday,
  refetchInterval: computed(() => isIntraday.value && pageVisible.value ? 30_000 : false),
})
const activeFilters = computed(() => ({
  page: currentPage.value, pageSize: historyView.value === 'events' && !isIntraday.value ? 30 : 20,
  keyword: textQuery(route.query.keyword), pivotType: textQuery(route.query.pivotType),
  stageAtEnd: textQuery(route.query.stageAtEnd), frequency: textQuery(route.query.frequency),
  activeAtEnd: route.query.activeAtEnd === 'true' ? true : route.query.activeAtEnd === 'false' ? false : undefined,
  ...(isIntraday.value ? {} : {
    dateFrom: textQuery(route.query.dateFrom, initialRange[0]), dateTo: textQuery(route.query.dateTo, initialRange[1]),
  }),
}))
const dateRangeDays = computed(() => {
  if (isIntraday.value) return 1
  const from = Date.parse(`${activeFilters.value.dateFrom}T00:00:00Z`)
  const to = Date.parse(`${activeFilters.value.dateTo}T00:00:00Z`)
  return Number.isFinite(from) && Number.isFinite(to) && to >= from ? Math.floor((to - from) / 86_400_000) + 1 : 0
})
const maximumDateRangeDays = computed(() => capabilities.data.value?.maximumDateRangeDays ?? 366)
const dateRangeValid = computed(() => isIntraday.value || (dateRangeDays.value > 0 && dateRangeDays.value <= maximumDateRangeDays.value))
const featureEnabled = computed(() => isIntraday.value
  ? capabilities.data.value?.intradayEnabled !== false : capabilities.data.value?.historicalDataEnabled !== false)
const intradayCanQuery = computed(() => !isIntraday.value || intradayStatus.data.value?.isTradingDay === true)

const groupsQuery = useQuery({
  queryKey: computed(() => ['pair-stock-groups', context.value, activeFilters.value]),
  queryFn: () => isIntraday.value ? marketApi.pairIntradayGroups(activeFilters.value) : marketApi.pairHistoricalDataGroups(activeFilters.value),
  enabled: computed(() => featureEnabled.value && intradayCanQuery.value && dateRangeValid.value
    && (isIntraday.value || historyView.value === 'groups')),
  refetchInterval: computed(() => isIntraday.value && pageVisible.value
    ? Math.max(capabilities.data.value?.intradayRefreshSeconds ?? 30, 10) * 1_000 : false),
  staleTime: 30_000,
  placeholderData: (previousData) => previousData,
})
const eventsQuery = useQuery({
  queryKey: computed(() => ['pair-timeline-events', activeFilters.value]),
  queryFn: () => marketApi.pairHistoricalDataEvents(activeFilters.value),
  enabled: computed(() => !isIntraday.value && featureEnabled.value && dateRangeValid.value && historyView.value === 'events'),
  staleTime: 30_000,
  placeholderData: (previousData) => previousData,
})
const groupColumns = [
  { title: '股票', key: 'stock', width: 170 }, { title: '最新顶底', key: 'latestPivotAt', width: 180 },
  { title: '最新顶部', key: 'latestTopAt', width: 160 }, { title: '最新底部', key: 'latestBottomAt', width: 160 },
  { title: '顶底数量', key: 'directions', width: 180 }, { title: '状态', key: 'states', width: 170 },
  { title: '合计', dataIndex: 'eventCount', key: 'eventCount', width: 72 },
]
const hasActiveFilter = computed(() => Boolean(
  activeFilters.value.keyword || activeFilters.value.pivotType || activeFilters.value.stageAtEnd
  || activeFilters.value.frequency || activeFilters.value.activeAtEnd !== undefined,
))
const watermarkFrequencies = ['5m', '30m', '60m', '1d'] as const
const sessionLabel = computed(() => ({
  PRE_OPEN: '盘前等待', MORNING_SESSION: '上午交易', MIDDAY_BREAK: '午间休市',
  AFTERNOON_SESSION: '下午交易', CLOSED: '已收盘', UNAVAILABLE: '状态不可用',
}[intradayStatus.data.value?.sessionStatus ?? 'UNAVAILABLE']))
const intradayEmpty = computed(() => {
  const session = intradayStatus.data.value?.sessionStatus
  if (session === 'PRE_OPEN') return { title: '等待开盘', description: '今天尚未形成对子顶底' }
  if (session === 'CLOSED') return { title: '本交易日没有记录', description: '今天未形成符合当前筛选条件的对子顶底' }
  return { title: '当前尚无记录', description: '新的对子顶底形成后会自动显示' }
})

function syncDraftFromRoute() {
  filterForm.keyword = textQuery(route.query.keyword); filterForm.pivotType = textQuery(route.query.pivotType)
  filterForm.stageAtEnd = textQuery(route.query.stageAtEnd); filterForm.frequency = textQuery(route.query.frequency)
  filterForm.activeAtEnd = textQuery(route.query.activeAtEnd)
  if (!isIntraday.value) dateRange.value = [textQuery(route.query.dateFrom, initialRange[0]), textQuery(route.query.dateTo, initialRange[1])]
}
watch(() => route.query, syncDraftFromRoute, { deep: true })
watch(context, (value) => {
  if (value === 'history-data' && (!textQuery(route.query.dateFrom) || !textQuery(route.query.dateTo))) {
    const range = defaultDateRange()
    void router.replace({ query: { ...route.query, dateFrom: range[0], dateTo: range[1] } })
  }
}, { immediate: true })
watch(() => JSON.stringify({ context: context.value, filters: activeFilters.value }), () => {
  expandedSymbols.value = []
  Object.keys(groupEvents).forEach((key) => delete groupEvents[key])
})
function navigateContext(target: PairTrendViewContext) {
  if (target !== context.value) void router.push({ name: target === 'intraday' ? 'pair-trends-intraday' : 'pair-trends-history-data' })
}
function setHistoryView(value: HistoryView) {
  void router.replace({ query: { ...route.query, view: value === 'events' ? 'events' : undefined, page: undefined, eventId: undefined } })
}
function applyFilters() {
  void router.replace({ query: {
    ...(isIntraday.value ? {} : { view: historyView.value === 'events' ? 'events' : undefined }),
    keyword: filterForm.keyword.trim() || undefined, pivotType: filterForm.pivotType || undefined,
    stageAtEnd: filterForm.stageAtEnd || undefined, frequency: filterForm.frequency || undefined,
    activeAtEnd: filterForm.activeAtEnd || undefined,
    dateFrom: isIntraday.value ? undefined : dateRange.value[0], dateTo: isIntraday.value ? undefined : dateRange.value[1],
  } })
}
function resetFilters() {
  const range = defaultDateRange()
  filterForm.keyword = ''; filterForm.pivotType = ''; filterForm.stageAtEnd = ''; filterForm.frequency = ''; filterForm.activeAtEnd = ''
  dateRange.value = range; applyFilters()
}
function emptyTitle(defaultTitle: string) { return hasActiveFilter.value ? '当前筛选无结果' : defaultTitle }
function emptyDescription(defaultDescription: string) {
  return hasActiveFilter.value ? '已保留当前数据范围，请重置或调整筛选条件。' : defaultDescription
}
function setPage(page: number) { void router.replace({ query: { ...route.query, page: page > 1 ? String(page) : undefined, eventId: undefined } }) }
function openDetail(id: number) { void router.push({ query: { ...route.query, eventId: String(id) } }) }
function closeDetail() { void router.replace({ query: { ...route.query, eventId: undefined } }) }
async function loadGroupEvents(symbol: string, nextPage = 1) {
  const state = ensureGroupEventState(groupEvents, symbol)
  if (state.loading) return
  state.loading = true; state.error = ''
  try {
    const filters = { ...activeFilters.value, keyword: undefined, page: nextPage, pageSize: 50 }
    const response = isIntraday.value
      ? await marketApi.pairIntradayGroupEvents(symbol, filters) : await marketApi.pairHistoricalDataGroupEvents(symbol, filters)
    state.items = nextPage === 1 ? response.items : [...state.items, ...response.items]
    state.page = response.page; state.total = response.total
  } catch (error) { state.error = error instanceof Error ? error.message : '组内记录加载失败' }
  finally { state.loading = false }
}
function handleExpand(expanded: boolean, record: PairTrendStockGroup) {
  expandedSymbols.value = expanded ? [record.symbol] : []
  if (expanded && !groupEvents[record.symbol]?.items.length) void loadGroupEvents(record.symbol)
}
function closeReplayWarning() { void router.replace({ query: { ...route.query, replayDisabled: undefined } }) }
function refreshVisibleData() {
  if (isIntraday.value) {
    void intradayStatus.refetch()
    if (intradayStatus.data.value?.isTradingDay === true) void groupsQuery.refetch()
  }
  else if (historyView.value === 'groups') void groupsQuery.refetch()
  else void eventsQuery.refetch()
}
function handleVisibilityChange() { pageVisible.value = !document.hidden; if (pageVisible.value && isIntraday.value) refreshVisibleData() }
function handleViewportChange(event: MediaQueryListEvent | MediaQueryList) { isMobile.value = event.matches }
onMounted(() => {
  document.addEventListener('visibilitychange', handleVisibilityChange)
  mobileMediaQuery = window.matchMedia('(max-width: 720px)')
  handleViewportChange(mobileMediaQuery)
  mobileMediaQuery.addEventListener('change', handleViewportChange)
})
onBeforeUnmount(() => {
  document.removeEventListener('visibilitychange', handleVisibilityChange)
  mobileMediaQuery?.removeEventListener('change', handleViewportChange)
})
</script>

<template>
  <section>
    <nav class="pair-source-tabs" aria-label="对子顶底数据范围">
      <button type="button" :class="{ active: isIntraday }" @click="navigateContext('intraday')">盘中实时</button>
      <button type="button" :class="{ active: !isIntraday }" @click="navigateContext('history-data')">历史数据</button>
      <button type="button" disabled title="历史回放已停用" aria-label="历史回放已停用">历史回放 <LockOutlined /></button>
    </nav>
    <a-alert v-if="route.query.replayDisabled === '1'" class="page-alert" type="warning" show-icon closable
      message="历史回放已停用" description="该入口不可操作，也不会请求历史回放接口。请使用历史数据查询。" @close="closeReplayWarning" />
    <a-alert v-if="capabilities.isError.value" class="page-alert" type="error" show-icon
      message="无法确认对子顶底功能状态" description="服务能力接口请求失败，请重试。" />
    <a-alert v-else-if="!featureEnabled" class="page-alert" type="warning" show-icon
      :message="isIntraday ? '盘中实时功能未启用' : '历史数据功能未启用'" />

    <div class="filter-bar pair-filter-bar">
      <div><span class="eyebrow">PAIR PIVOTS</span><h2>{{ isIntraday ? '盘中实时' : '历史数据' }}</h2>
        <p v-if="isIntraday">
          <span v-if="intradayStatus.data.value">
            交易日 {{ intradayStatus.data.value.tradingDate }} · {{ sessionLabel }} · 采集 {{ intradayStatus.data.value.collectionStatus }}
            <template v-if="intradayStatus.data.value.lastUpdatedAt"> · 数据更新 {{ formatTime(intradayStatus.data.value.lastUpdatedAt) }}</template>
          </span>
          <span v-else>正在确认服务端交易日期</span>
        </p>
        <p v-else>按后端顶底形成时间查询；默认最近 60 天</p>
      </div>
      <div class="filter-controls pair-filter-controls">
        <a-segmented v-if="!isIntraday" :value="historyView" :options="[{label:'按股票分组',value:'groups'},{label:'事件列表',value:'events'}]"
          @change="(value: string | number) => setHistoryView(String(value) as HistoryView)" />
        <a-input-search v-model:value="filterForm.keyword" allow-clear placeholder="股票名称或代码" class="stock-search" @search="applyFilters" />
        <a-select v-model:value="filterForm.pivotType" class="filter-select" :options="[{label:'全部方向',value:''},{label:'顶部',value:'TOP'},{label:'底部',value:'BOTTOM'}]" />
        <a-select v-model:value="filterForm.frequency" class="filter-select frequency-select" :options="[{label:'全部周期',value:''},...['5m','30m','60m','1d'].map(value=>({label:value,value}))]" />
        <a-select v-model:value="filterForm.stageAtEnd" class="stage-select" :options="[
          {label:'全部阶段',value:''},{label:'发现',value:'DISCOVERED'},{label:'观察',value:'OBSERVING'},
          {label:'重点',value:'FOCUS'},{label:'成立',value:'ESTABLISHED'},{label:'失效',value:'INVALIDATED'},
        ]" />
        <a-select v-model:value="filterForm.activeAtEnd" class="validity-select" :options="[{label:'全部有效性',value:''},{label:'有效',value:'true'},{label:'已失效',value:'false'}]" />
        <a-range-picker v-if="!isIntraday" v-model:value="dateRange" value-format="YYYY-MM-DD" format="YYYY-MM-DD"
          :allow-clear="false" :placeholder="['开始日期','结束日期']" class="date-range" />
        <a-button type="primary" :loading="groupsQuery.isFetching.value || eventsQuery.isFetching.value" @click="applyFilters">查询</a-button>
        <a-button type="text" @click="resetFilters">重置</a-button>
        <a-button type="text" :aria-label="isIntraday ? '刷新盘中数据' : '刷新历史数据'" @click="refreshVisibleData"><ReloadOutlined /></a-button>
      </div>
    </div>

    <a-alert v-if="!dateRangeValid" class="page-alert" type="warning" show-icon :message="`查询日期必须连续且不超过 ${maximumDateRangeDays} 天`" />
    <a-alert v-if="isIntraday && intradayStatus.isError.value" class="page-alert" type="error" show-icon
      message="盘中交易日状态加载失败" description="无法确定服务端交易日期，因此没有查询或回退到历史数据。" />
    <a-alert v-else-if="isIntraday && intradayStatus.data.value?.marketDayStatus === 'CALENDAR_PENDING'" class="page-alert" type="warning" show-icon
      message="交易日历尚未同步" description="服务端暂时无法确认今天是否为交易日，不会回退展示上一交易日。" />
    <section v-if="isIntraday && intradayStatus.data.value?.isTradingDay === true" class="intraday-watermarks" aria-label="盘中采集水位">
      <header>
        <div><span>采集状态</span><strong>{{ intradayStatus.data.value.collectionStatus }}</strong></div>
        <time class="numeric">检查于 {{ formatTime(intradayStatus.data.value.checkedAt) }}</time>
      </header>
      <div class="watermark-grid">
        <div v-for="frequency in watermarkFrequencies" :key="frequency">
          <span>{{ frequency }} 水位</span>
          <strong class="numeric">{{ intradayStatus.data.value.watermarks[frequency] ? formatTime(intradayStatus.data.value.watermarks[frequency]) : '尚无水位' }}</strong>
        </div>
      </div>
      <p>四周期分别展示已提交进度；部分周期处理中时，其余周期水位仍保持可见。</p>
    </section>
    <EmptyState v-if="isIntraday && intradayStatus.data.value?.marketDayStatus === 'NON_TRADING_DAY'"
      title="今日非交易日" description="盘中实时页不展示历史数据，请切换到“历史数据”查询。" />
    <a-alert v-else-if="(groupsQuery.isError.value && (isIntraday || historyView === 'groups')) || (eventsQuery.isError.value && !isIntraday && historyView === 'events')"
      class="page-alert" type="error" show-icon message="对子顶底查询失败" description="这不是无数据状态。请检查接口服务后重试。" />

    <template v-else-if="featureEnabled && dateRangeValid && intradayCanQuery">
      <template v-if="isIntraday || historyView === 'groups'">
        <template v-if="groupsQuery.isLoading.value || (groupsQuery.data.value?.groups.length ?? 0) > 0">
        <a-table v-if="!isMobile"
          :columns="groupColumns" :data-source="groupsQuery.data.value?.groups ?? []" :loading="groupsQuery.isLoading.value"
          :pagination="false" :expanded-row-keys="expandedSymbols" row-key="symbol" class="market-table pair-group-table pair-group-desktop"
          :scroll="{ x: 1180 }" @expand="handleExpand">
          <template #bodyCell="{ column, record }">
            <template v-if="column.key === 'stock'"><div class="stock-cell"><strong>{{ record.symbolName || record.symbol }}</strong><span>{{ record.symbol }}</span></div></template>
            <template v-else-if="column.key === 'latestPivotAt'"><span class="numeric">{{ formatTime(record.latestPivotAt) }}</span><small class="latest-stage">{{ label(record.latestStageAtEnd) }}</small></template>
            <template v-else-if="column.key === 'latestTopAt'"><span class="numeric pair-price-top">{{ record.latestTopAt ? formatTime(record.latestTopAt) : '—' }}</span></template>
            <template v-else-if="column.key === 'latestBottomAt'"><span class="numeric pair-price-bottom">{{ record.latestBottomAt ? formatTime(record.latestBottomAt) : '—' }}</span></template>
            <template v-else-if="column.key === 'directions'"><a-tag class="pair-top">顶部 {{ record.topCount }}</a-tag><a-tag class="pair-bottom">底部 {{ record.bottomCount }}</a-tag></template>
            <template v-else-if="column.key === 'states'"><span class="group-state active">有效 {{ record.activeAtEndCount }}</span><span class="group-state invalidated">失效 {{ record.invalidatedAtEndCount }}</span></template>
          </template>
          <template #expandedRowRender="{ record }">
            <div class="group-events-panel">
              <a-alert v-if="groupEvents[record.symbol]?.error" type="error" show-icon :message="groupEvents[record.symbol].error">
                <template #action><a-button size="small" @click="loadGroupEvents(record.symbol, 1)">重试</a-button></template>
              </a-alert>
              <PairTrendTimelineTable v-else :items="groupEvents[record.symbol]?.items ?? []"
                :loading="groupEvents[record.symbol]?.loading ?? true" @open="openDetail" />
              <div v-if="groupEvents[record.symbol] && groupEvents[record.symbol].items.length < groupEvents[record.symbol].total" class="load-more">
                <a-button :loading="groupEvents[record.symbol].loading" @click="loadGroupEvents(record.symbol, groupEvents[record.symbol].page + 1)">
                  加载更多（{{ groupEvents[record.symbol].items.length }}/{{ groupEvents[record.symbol].total }}）
                </a-button>
              </div>
            </div>
          </template>
        </a-table>
        <div v-else class="pair-group-mobile">
          <a-skeleton v-if="groupsQuery.isLoading.value" active :paragraph="{ rows: 6 }" />
          <article v-for="record in groupsQuery.data.value?.groups ?? []" :key="record.symbol" class="pair-group-card">
            <button type="button" class="pair-group-card-toggle" :aria-expanded="expandedSymbols.includes(record.symbol)" @click="handleExpand(!expandedSymbols.includes(record.symbol), record)">
              <header><div class="stock-cell"><strong>{{ record.symbolName || record.symbol }}</strong><span>{{ record.symbol }}</span></div><span class="expand-hint">{{ expandedSymbols.includes(record.symbol) ? '收起' : '展开' }}</span></header>
              <div class="mobile-latest"><span>最新顶底</span><strong class="numeric">{{ formatTime(record.latestPivotAt) }}</strong><em>{{ label(record.latestStageAtEnd) }}</em></div>
              <div class="pair-group-card-grid">
                <div><span>最新顶部</span><strong class="numeric pair-price-top">{{ record.latestTopAt ? formatTime(record.latestTopAt) : '—' }}</strong></div>
                <div><span>最新底部</span><strong class="numeric pair-price-bottom">{{ record.latestBottomAt ? formatTime(record.latestBottomAt) : '—' }}</strong></div>
                <div><span>顶 / 底</span><strong>{{ record.topCount }} / {{ record.bottomCount }}</strong></div>
                <div><span>有效 / 失效</span><strong>{{ record.activeAtEndCount }} / {{ record.invalidatedAtEndCount }}</strong></div>
              </div>
            </button>
            <div v-if="expandedSymbols.includes(record.symbol)" class="group-events-panel mobile-group-events">
              <a-alert v-if="groupEvents[record.symbol]?.error" type="error" show-icon :message="groupEvents[record.symbol].error">
                <template #action><a-button size="small" @click="loadGroupEvents(record.symbol, 1)">重试</a-button></template>
              </a-alert>
              <PairTrendTimelineTable v-else :items="groupEvents[record.symbol]?.items ?? []" :loading="groupEvents[record.symbol]?.loading ?? true" @open="openDetail" />
              <div v-if="groupEvents[record.symbol] && groupEvents[record.symbol].items.length < groupEvents[record.symbol].total" class="load-more">
                <a-button :loading="groupEvents[record.symbol].loading" @click="loadGroupEvents(record.symbol, groupEvents[record.symbol].page + 1)">加载更多（{{ groupEvents[record.symbol].items.length }}/{{ groupEvents[record.symbol].total }}）</a-button>
              </div>
            </div>
          </article>
        </div>
        </template>
        <EmptyState v-else-if="groupsQuery.isSuccess.value"
          :title="emptyTitle(isIntraday ? intradayEmpty.title : '所选日期范围没有记录')"
          :description="emptyDescription(isIntraday ? intradayEmpty.description : '该日期范围内没有对子顶底记录。')"
          :action-text="hasActiveFilter ? '重置筛选' : undefined" @action="resetFilters" />
        <a-pagination v-if="(groupsQuery.data.value?.total ?? 0) > 0" class="table-pagination" :current="currentPage"
          :page-size="20" :total="groupsQuery.data.value?.total ?? 0" show-less-items @change="setPage" />
      </template>
      <template v-else>
        <PairTrendTimelineTable v-if="eventsQuery.isLoading.value || (eventsQuery.data.value?.items.length ?? 0) > 0"
          :items="eventsQuery.data.value?.items ?? []" :loading="eventsQuery.isLoading.value" show-stock @open="openDetail" />
        <EmptyState v-else-if="eventsQuery.isSuccess.value" :title="emptyTitle('所选日期范围没有记录')"
          :description="emptyDescription('该日期范围内没有对子顶底记录。')"
          :action-text="hasActiveFilter ? '重置筛选' : undefined" @action="resetFilters" />
        <a-pagination v-if="(eventsQuery.data.value?.total ?? 0) > 0" class="table-pagination" :current="currentPage"
          :page-size="30" :total="eventsQuery.data.value?.total ?? 0" show-less-items @change="setPage" />
      </template>
    </template>
    <PairTrendDetailDrawer v-if="eventId > 0" :open="true" :context="context" :event-id="eventId" @close="closeDetail" />
  </section>
</template>

<style scoped>
.pair-source-tabs { display:flex; width:fit-content; margin:2px 0 22px; padding:4px; gap:4px; border:1px solid #243248; border-radius:10px; background:#0f1929; }
.pair-source-tabs button { min-width:112px; padding:8px 14px; color:#8393aa; border:0; border-radius:7px; background:transparent; cursor:pointer; }
.pair-source-tabs button:hover:not(:disabled) { color:#dce4ef; background:#172338; }.pair-source-tabs button.active { color:#fff; background:#6759d1; }
.pair-source-tabs button:disabled { color:#4f5d72; cursor:not-allowed; }.page-alert { margin-bottom:16px; }.pair-filter-bar { align-items:flex-start; }
.pair-filter-controls { max-width:1120px; flex-wrap:wrap; justify-content:flex-end; }.stock-search { width:200px; }.filter-select { width:116px; }
.frequency-select { width:108px; }.stage-select { width:124px; }.validity-select { width:124px; }.date-range { width:250px; }
.intraday-watermarks { margin:0 0 16px; padding:14px; border:1px solid #243248; border-radius:10px; background:#0f1929; }
.intraday-watermarks header { display:flex; align-items:center; justify-content:space-between; gap:14px; }.intraday-watermarks header span { margin-right:8px; color:#71829a; font-size:11px; }
.intraday-watermarks header strong { color:#dce4ef; }.intraday-watermarks header time { color:#71829a; font-size:11px; }
.watermark-grid { display:grid; grid-template-columns:repeat(4,minmax(0,1fr)); gap:8px; margin-top:12px; }.watermark-grid div { padding:9px 10px; border-radius:7px; background:#0b1423; }
.watermark-grid span,.watermark-grid strong { display:block; }.watermark-grid span { color:#71829a; font-size:10px; }.watermark-grid strong { margin-top:4px; color:#cdd6e2; font-size:12px; }
.intraday-watermarks p { margin:9px 0 0; color:#65758e; font-size:10px; }
.latest-stage { display:block; margin-top:3px; color:#9b8ff8; font-size:10px; }.pair-price-top { color:#73d13d; }.pair-price-bottom { color:#ff7875; }
.group-state { margin-right:12px; font-size:12px; }.group-state.active { color:#52c41a; }.group-state.invalidated { color:#8a98ac; }
.group-events-panel { padding:4px 2px 10px; }.load-more { padding-top:12px; text-align:center; }
.pair-group-mobile { display:none; }
@media (max-width:1100px) { .pair-filter-bar { display:block; }.pair-filter-controls { margin-top:16px; justify-content:flex-start; } }
@media (max-width:720px) {
  .pair-source-tabs { width:100%; }.pair-source-tabs button { flex:1; min-width:0; padding-inline:7px; }
  .stock-search,.filter-select,.frequency-select,.stage-select,.validity-select,.date-range { width:100%; }
  .pair-filter-controls { display:grid; grid-template-columns:1fr 1fr; }
  .pair-filter-controls :deep(.ant-segmented),.pair-filter-controls .stock-search,.pair-filter-controls .date-range { grid-column:1/-1; }
  .intraday-watermarks header { align-items:flex-start; flex-direction:column; }.watermark-grid { grid-template-columns:1fr 1fr; }
  .pair-group-desktop { display:none; }.pair-group-mobile { display:grid; gap:10px; }
  .pair-group-card { overflow:hidden; border:1px solid #243248; border-radius:10px; background:#111a2b; }
  .pair-group-card-toggle { width:100%; padding:14px; color:inherit; text-align:left; border:0; background:transparent; cursor:pointer; }
  .pair-group-card-toggle header { display:flex; align-items:center; justify-content:space-between; gap:10px; }.expand-hint { color:#8f83e8; font-size:11px; }
  .mobile-latest { display:grid; grid-template-columns:auto 1fr auto; align-items:center; gap:8px; margin-top:12px; padding:9px; border-radius:7px; background:#0d1626; }
  .mobile-latest span { color:#71829a; font-size:10px; }.mobile-latest strong { color:#dce4ef; font-size:12px; }.mobile-latest em { color:#9b8ff8; font-size:10px; font-style:normal; }
  .pair-group-card-grid { display:grid; grid-template-columns:1fr 1fr; gap:8px; margin-top:8px; }.pair-group-card-grid div { padding:8px; border-radius:7px; background:#0d1626; }
  .pair-group-card-grid span,.pair-group-card-grid strong { display:block; }.pair-group-card-grid span { color:#71829a; font-size:10px; }.pair-group-card-grid strong { margin-top:3px; font-size:11px; }
  .mobile-group-events { padding:10px; border-top:1px solid #243248; background:#0b1423; }
}
</style>
