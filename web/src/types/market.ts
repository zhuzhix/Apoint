export interface NotificationTask {
  schemaVersion: number
  id: number
  taskKey: string
  taskType: 'strategy_opportunity' | 'pair_trend' | string
  sourceId: string
  symbol?: string
  symbolName?: string
  severity: string
  businessStatus: string
  revision: number
  latestEventId: string
  title: string
  summary: string
  payloadJson: string
  isRead: boolean
  isStarred: boolean
  userStatus: 'active' | 'handled' | 'archived'
  firstSeenAt: string
  lastSeenAt: string
  readAt?: string
  handledAt?: string
  archivedAt?: string
  createdAt: string
  updatedAt: string
}

export interface PageResponse<T> {
  page: number
  pageSize: number
  total: number
  totalPages: number
  items: T[]
  highWatermark?: number
}

export interface NotificationChange {
  changeId: number
  changeType: string
  eventId: string
  revision: number
  occurredAt: string
  task: NotificationTask
}

export interface NotificationChangePage {
  afterId: number
  highWatermark: number
  hasMore: boolean
  items: NotificationChange[]
}

export interface PairTrendEvent {
  id: number
  eventKey: string
  symbol: string
  symbolName?: string
  pivotType: 'TOP' | 'BOTTOM'
  status: string
  firstSeenAt: string
  lastSeenAt: string
  confirmedAt?: string
  latestPairPrice: number
  priceTicks?: number
  latestPairCode: number
  latestPairKind: 'ROUND_00' | 'DOUBLE_DIGIT'
  frequencies: string
  strongestFrequency: string
  confluenceCount: number
  totalHitCount: number
  confirmedHitCount: number
  invalidatedHitCount: number
  pendingHitCount: number
  retractedHitCount?: number
  round00HitCount?: number
  doubleDigitHitCount?: number
  score: number
  maxTrendStrength: number
  algorithmVersion: string
  stage: 'DISCOVERED' | 'OBSERVING' | 'FOCUS' | 'ESTABLISHED' | 'INVALIDATED'
  generation: number
  isActive: boolean
  discoveredAt?: string
  observedAt?: string
  focusedAt?: string
  establishedAt?: string
  invalidatedAt?: string
  invalidatedPrice?: number
  invalidationReason?: string
  rootFiveMinuteBob?: string
  rootFiveMinuteEob?: string
  lastTransitionAt?: string
  eventRevision?: number
  lastSourceEventId?: string
  runId?: number
  timeframeMask?: number
  summaryJson?: string
  createdAt?: string
  updatedAt?: string
}

export type PairTrendSource = 'live' | 'history'
export type PairTrendViewContext = 'intraday' | 'history-data'

export interface PairTrendCapabilities {
  historicalDataEnabled: boolean
  intradayEnabled: boolean
  historicalReplayEnabled: boolean
  timeZone: string
  intradayRefreshSeconds: number
  maximumDateRangeDays: number
}

export type PairTrendStage = 'DISCOVERED' | 'OBSERVING' | 'FOCUS' | 'ESTABLISHED' | 'INVALIDATED'

export interface PairTrendStockGroup {
  symbol: string
  symbolName?: string
  latestPivotAt: string
  latestTopAt?: string
  latestBottomAt?: string
  latestStageAtEnd: PairTrendStage
  eventCount: number
  topCount: number
  bottomCount: number
  activeAtEndCount: number
  invalidatedAtEndCount: number
}

export interface PairTrendStockGroupPage {
  page: number
  pageSize: number
  total: number
  totalPages: number
  groups: PairTrendStockGroup[]
}

export interface PairTrendTimelineEvent {
  id: number
  eventKey: string
  symbol: string
  symbolName?: string
  pivotAt: string
  pivotType: 'TOP' | 'BOTTOM'
  pairPrice: number
  pairKind: 'ROUND_00' | 'DOUBLE_DIGIT'
  generation: number
  frequencies: string
  strongestFrequency: string
  stageAtEnd: PairTrendStage
  isActiveAtEnd: boolean
  currentStage: PairTrendStage
  currentIsActive: boolean
  observedAt?: string
  focusedAt?: string
  establishedAt?: string
  invalidatedAt?: string
  invalidationReason?: string
  lastTransitionAt?: string
  waveCalculationStatus: 'NOT_ELIGIBLE' | 'PENDING' | 'COLLECTING' | 'COMPLETED' | 'INSUFFICIENT_DATA' | 'FAILED'
  waveSignal?: 'NONE' | 'CANDIDATE' | 'STRONG'
  waveScore?: number
  waveEvaluatedAt?: string
  waveDataAsOf?: string
  waveAlgorithmVersion?: string
}

export interface PairTrendTimelineEventPage extends PageResponse<PairTrendTimelineEvent> {
  symbol?: string
  symbolName?: string
}

export interface PairTrendIntradayStatus {
  tradingDate: string
  isTradingDay: boolean | null
  marketDayStatus: 'TRADING_DAY' | 'NON_TRADING_DAY' | 'CALENDAR_PENDING'
  sessionStatus: 'PRE_OPEN' | 'MORNING_SESSION' | 'MIDDAY_BREAK' | 'AFTERNOON_SESSION' | 'CLOSED' | 'UNAVAILABLE'
  collectionStatus: string
  watermarks: Record<string, string>
  checkedAt: string
  lastUpdatedAt?: string
}

export interface PairTrendHit {
  id: number
  runId?: number
  eventId?: number
  hitKey: string
  symbol: string
  frequency: string
  tradingDate: string
  bob: string
  eob: string
  observedAt: string
  confirmedAt?: string
  pivotType: 'TOP' | 'BOTTOM'
  status: string
  pairPrice: number
  priceTicks?: number
  pairCode: number
  pairKind: 'ROUND_00' | 'DOUBLE_DIGIT'
  hitField: string
  trendDirection: string
  trendStrength: number
  ema20: number
  ema60: number
  atr14: number
  previousClose?: number
  openPrice: number
  highPrice: number
  lowPrice: number
  closePrice: number
  volume: number
  amount: number
  isRollingExtreme: boolean
  volumePercentile: number
  wickRatio: number
  reversalAtr: number
  score: number
  confirmationReason?: string
  sourceRevision?: number
  sourceRowHash?: string
  sourceEventId?: string
  algorithmVersion: string
  stage?: string
  isPromotion?: boolean
  detailsJson?: string
  createdAt?: string
  updatedAt?: string
}

export interface PairTrendSourceInfo {
  runId?: number
  runMode: string
  dataSource: string
  isAcceptanceSample: boolean
  notes?: string
}

export interface PairTrendChartWindow {
  frequency: string
  from: string
  to: string
}

export interface PairTrendDetail {
  source: PairTrendSource
  sourceInfo: PairTrendSourceInfo
  pairEvent: PairTrendEvent
  hits: PageResponse<PairTrendHit>
  lifecycles: PairTrendLifecycle[]
  recommendedChart: PairTrendChartWindow
}

export interface PairTrendLifecycle {
  id: number
  lifecycleKey: string
  fromStage?: string
  toStage: string
  occurredAt: string
  triggerFrequency: string
  triggerPrice: number
  reason: string
  sourceRowHash: string
  shouldNotify: boolean
}

export interface PairChartMarker {
  id: number
  time: string
  price: number
  pivotType: 'TOP' | 'BOTTOM'
  status: string
  pairCode: number
  pairKind: string
  score: number
  selected?: boolean
}

export interface MarketBar {
  symbol: string
  frequency: string
  tradingDate: string
  bob: string
  eob: string
  openPrice: number
  highPrice: number
  lowPrice: number
  closePrice: number
  preClose?: number
  volume: number
  amount: number
  isClosed: boolean
  revision: number
  source: string
  officialConfirmed: boolean
  qualityStatus: string
}

export interface StrategyPayload {
  opportunityId?: number
  tradingDate?: string
  level?: string
  status?: string
  primaryStrategyCode?: string
  primaryStrategyName?: string
  highestScore?: number
  strategyCount?: number
  eventType?: string
  action?: string
  confidence?: string
  hitPrice?: number
  stopReference?: number
  targetReference?: number
  passedConditions?: string[]
  sourceWatermark?: string
}

export interface PairPayload {
  pairEventId?: number
  eventKey?: string
  pivotType?: string
  status?: string
  latestPairPrice?: number
  latestPairCode?: number
  latestPairKind?: string
  frequencies?: string[]
  strongestFrequency?: string
  confluenceCount?: number
  totalHitCount?: number
  confirmedHitCount?: number
  pendingHitCount?: number
  invalidatedHitCount?: number
  retractedHitCount?: number
  score?: number
  maxTrendStrength?: number
  algorithmVersion?: string
  eventRevision?: number
  stage?: string
  generation?: number
  isActive?: boolean
  invalidatedAt?: string
  invalidatedPrice?: number
  invalidationReason?: string
}

export function parsePayload<T>(task: NotificationTask): T {
  try { return JSON.parse(task.payloadJson) as T }
  catch { return {} as T }
}
