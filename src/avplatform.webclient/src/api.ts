import type {
  ChannelDetail,
  ChannelHome,
  ChannelPlay,
  ChannelSearch,
  ChannelSummary,
} from './types'

async function request<T>(path: string): Promise<T> {
  const response = await fetch(path, { headers: { Accept: 'application/json' } })
  if (!response.ok) {
    const problem = (await response.json().catch(() => null)) as { title?: string } | null
    throw new Error(problem?.title ?? `接口调用失败：${response.status} ${response.statusText}`)
  }
  return (await response.json()) as T
}

export function getChannels() {
  return request<ChannelSummary[]>('/api/channels')
}

export function getChannelHome(code: string, refresh = false) {
  return request<ChannelHome>(`/api/channels/${encodeURIComponent(code)}/home?refresh=${refresh}`)
}

export function searchChannel(code: string, query: string, refresh = false) {
  return request<ChannelSearch>(
    `/api/channels/${encodeURIComponent(code)}/search?q=${encodeURIComponent(query)}&refresh=${refresh}`,
  )
}

export function getChannelDetail(code: string, itemId: string, refresh = false) {
  return request<ChannelDetail>(
    `/api/channels/${encodeURIComponent(code)}/items/${encodeURIComponent(itemId)}?refresh=${refresh}`,
  )
}

export function getEpisodePlay(code: string, itemId: string, episodeId: string) {
  return request<ChannelPlay>(
    `/api/channels/${encodeURIComponent(code)}/items/${encodeURIComponent(itemId)}/episodes/${encodeURIComponent(episodeId)}/play`,
  )
}
