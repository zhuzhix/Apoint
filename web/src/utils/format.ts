export function formatTime(value?: string) {
  if (!value) return '—'
  return new Intl.DateTimeFormat('zh-CN', {
    month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit', hour12: false,
  }).format(new Date(value))
}

export function formatRelativeTime(value?: string) {
  if (!value) return '—'
  const seconds = Math.max(0, Math.floor((Date.now() - new Date(value).getTime()) / 1000))
  if (seconds < 10) return '刚刚'
  if (seconds < 60) return `${seconds}秒前`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}分钟前`
  if (seconds < 86_400) return `${Math.floor(seconds / 3600)}小时前`
  return `${Math.floor(seconds / 86_400)}天前`
}

export function score(value?: number) { return value === undefined ? '—' : Number(value).toFixed(2) }
export function price(value?: number) { return value === undefined ? '—' : Number(value).toFixed(2) }

const labels: Record<string, string> = {
  focus: '重点', candidate: '候选', observe: '观察', active: '活跃', weakened: '减弱', expired: '失效',
  confirmed: '已确认', invalidated: '已失效', top: '顶部', bottom: '底部',
  discovered: '发现', observing: '观察', established: '成立', resolved: '已解除',
  level1: '一级警报', critical: '特别提醒',
  new: '新发现', repeated: '重复', strengthened: '加强', revised: '修订',
}
export function label(value?: string) { return value ? labels[value.toLowerCase()] ?? value : '—' }
