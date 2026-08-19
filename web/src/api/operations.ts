import { api } from './client'
import type { OperationsStatusResponse } from '@/types/operations'

export const operationsApi = {
  status: () => api.get<OperationsStatusResponse>('/api/operations/status'),
}
