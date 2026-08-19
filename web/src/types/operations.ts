export type OperationsHealthStatus =
  | 'healthy'
  | 'degraded'
  | 'unhealthy'
  | 'unknown'
  | 'online'
  | 'offline'
  | 'running'
  | 'stopped'

export interface OperationsCollectorProcess {
  workerId: string
  pid?: number
  status: string
  assignedSymbols: number
  completedSymbols: number
  failedSymbols: number
  currentSymbol?: string
  lastError?: string
}

export interface OperationsCollectorStatus {
  status: string
  lastHeartbeatAt?: string
  processesExpected: number
  processesRunning: number
  activeJobs: number
  queuedJobs: number
  succeededJobs: number
  retryingJobs: number
  failedJobs: number
  blacklistedSymbols: number
  processes: OperationsCollectorProcess[]
}

export interface OperationsCollectorInstance extends OperationsCollectorStatus {
  collectorId: string
  instanceId: string
  health: string
  lastHeartbeatAt: string
  heartbeatAgeSeconds: number
  cyclesCompleted: number
  currentCycleId?: string
  hostName?: string
  version?: string
  startedAt?: string
  lastError?: string
}

export interface OperationsApiStatus {
  status: string
  service: string
  version: string
  uptimeSeconds: number
  responseTimeMs: number
  lastErrorAt?: string
}

export interface OperationsWebsiteStatus {
  status: string
  service: string
  url: string
  responseTimeMs: number
  lastCheckedAt: string
  indexFileExists: boolean
  staticAssetCount: number
}

export interface PairTrendCollectionStatus {
  tradingDate?: string
  status: string
  activeCycleId?: string
  lastCompletedAt?: string
  lastError?: string
  watermarks: Record<string, string>
  symbolsInMemory: number
  barsInMemory: number
  lastErrorAt?: string
}

export interface OperationsBlacklistItem {
  symbol: string
  failureCount: number
  reason: string
  blacklistedAt: string
  expiresAt: string
}

export interface OperationsBlacklistStatus {
  activeSymbols: number
  recent: OperationsBlacklistItem[]
}

export interface OperationsDatabaseStatus {
  status: string
  responseTimeMs: number
}

export interface OperationsRecentError {
  source: string
  message: string
  occurredAt: string
}

export interface OperationsStatusResponse {
  checkedAt: string
  overallStatus: string
  collector: OperationsCollectorStatus
  api: OperationsApiStatus
  website: OperationsWebsiteStatus
  collection: PairTrendCollectionStatus
  collectors: OperationsCollectorInstance[]
  blacklist: OperationsBlacklistStatus
  database: OperationsDatabaseStatus
  recentErrors: OperationsRecentError[]
}
