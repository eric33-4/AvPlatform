export interface ChannelSummary {
  code: string
  name: string
  mode: string
  enabled: boolean
}

export interface ChannelItem {
  id: string
  title: string
  url?: string
  coverUrl?: string
  summary?: string
  kind: string
  author?: string
  episodeCount?: number
  popularity?: number
}

export interface ChannelHome {
  channelCode: string
  channelName: string
  mode: string
  fetchedAt: string
  fromCache: boolean
  items: ChannelItem[]
  notice?: string
}

export interface ChannelSearch {
  channelCode: string
  query: string
  fetchedAt: string
  fromCache: boolean
  items: ChannelItem[]
  notice?: string
}

export interface ChannelEpisode {
  id: string
  title: string
  duration?: string
  isFree: boolean
  isPlayable: boolean
}

export interface ChannelDetail {
  channelCode: string
  channelName: string
  id: string
  title: string
  coverUrl?: string
  summary?: string
  category?: string
  author?: string
  episodeCount: number
  popularity?: number
  isFinished: boolean
  isPaid: boolean
  price?: number
  fetchedAt: string
  fromCache: boolean
  episodes: ChannelEpisode[]
}

export interface ChannelPlay {
  channelCode: string
  contentId: string
  episodeId: string
  title: string
  mediaUrl: string
  mediaType: string
  mediaKind: 'audio' | 'video'
}
