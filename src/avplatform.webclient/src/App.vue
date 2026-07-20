<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import MediaPlayer from './components/MediaPlayer.vue'
import ContentDetail from './components/ContentDetail.vue'
import ContentGrid from './components/ContentGrid.vue'
import {
  getChannelDetail,
  getChannelHome,
  getChannels,
  getEpisodePlay,
  searchChannel,
} from './api'
import type {
  ChannelDetail,
  ChannelEpisode,
  ChannelHome,
  ChannelItem,
  ChannelPlay,
  ChannelSearch,
  ChannelSummary,
} from './types'

const channels = ref<ChannelSummary[]>([])
const selectedCode = ref('')
const home = ref<ChannelHome | null>(null)
const search = ref<ChannelSearch | null>(null)
const detail = ref<ChannelDetail | null>(null)
const play = ref<ChannelPlay | null>(null)
const query = ref('')
const loading = ref(false)
const error = ref('')

const selectedChannel = computed(() =>
  channels.value.find((channel) => channel.code === selectedCode.value),
)
const visibleItems = computed(() => search.value?.items ?? home.value?.items ?? [])
const resultNotice = computed(() => search.value?.notice ?? home.value?.notice)
const resultMeta = computed(() => search.value ?? home.value)

async function run(action: () => Promise<void>, fallback: string) {
  loading.value = true
  error.value = ''
  try {
    await action()
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : fallback
  } finally {
    loading.value = false
  }
}

async function loadChannels() {
  await run(async () => {
    channels.value = await getChannels()
    const firstChannel = channels.value.at(0)
    if (firstChannel) {
      selectedCode.value = firstChannel.code
      await loadHome(false)
    }
  }, '渠道列表加载失败')
}

async function loadHome(refresh: boolean) {
  if (!selectedCode.value) return
  await run(async () => {
    home.value = await getChannelHome(selectedCode.value, refresh)
    search.value = null
    detail.value = null
    if (refresh) play.value = null
  }, '渠道首页加载失败')
}

async function submitSearch(refresh = false) {
  const keyword = query.value.trim()
  if (!keyword) {
    await loadHome(refresh)
    return
  }
  await run(async () => {
    search.value = await searchChannel(selectedCode.value, keyword, refresh)
    detail.value = null
  }, '搜索失败')
}

async function loadDetail(itemId: string, refresh = false) {
  await run(async () => {
    detail.value = await getChannelDetail(selectedCode.value, itemId, refresh)
  }, '详情加载失败')
}

async function openItem(item: ChannelItem) {
  await loadDetail(item.id)
}

async function playEpisode(episode: ChannelEpisode) {
  if (!detail.value) return
  await run(async () => {
    play.value = await getEpisodePlay(selectedCode.value, detail.value!.id, episode.id)
  }, '播放信息加载失败')
}

async function selectChannel(code: string) {
  selectedCode.value = code
  query.value = ''
  play.value = null
  await loadHome(false)
}

async function refreshCurrent() {
  if (detail.value) {
    await loadDetail(detail.value.id, true)
  } else if (search.value) {
    await submitSearch(true)
  } else {
    await loadHome(true)
  }
}

onMounted(loadChannels)
</script>

<template>
  <div class="app-shell">
    <header class="topbar">
      <div>
        <p class="eyebrow">AV PLATFORM / MULTI CHANNEL</p>
        <h1>多渠道内容聚合</h1>
      </div>
      <div class="system-state"><span class="state-dot"></span>真实 API 已接入</div>
    </header>

    <main class="layout">
      <aside class="channel-panel" aria-label="渠道列表">
        <div class="section-heading"><span>渠道</span><strong>{{ channels.length }}</strong></div>
        <button
          v-for="channel in channels"
          :key="channel.code"
          class="channel-button"
          :class="{ active: channel.code === selectedCode }"
          type="button"
          @click="selectChannel(channel.code)"
        >
          <span class="channel-code">{{ channel.code.toUpperCase() }}</span>
          <span><strong>{{ channel.name }}</strong><small>{{ channel.mode }}</small></span>
        </button>
      </aside>

      <section class="content-panel">
        <div class="content-header">
          <div>
            <p class="eyebrow">CURRENT CHANNEL</p>
            <h2>{{ selectedChannel?.name ?? '等待渠道' }}</h2>
          </div>
          <button class="refresh-button" type="button" :disabled="loading" @click="refreshCurrent">
            {{ loading ? '加载中…' : '强制刷新' }}
          </button>
        </div>

        <form v-if="!detail" class="search-bar" @submit.prevent="submitSearch(false)">
          <input v-model="query" type="search" placeholder="在当前推荐内容中搜索" aria-label="搜索内容" />
          <button type="submit">搜索</button>
        </form>

        <p v-if="error" class="error-message">{{ error }}</p>

        <ContentDetail
          v-if="detail"
          :detail="detail"
          @back="detail = null"
          @play="playEpisode"
        />
        <template v-else>
          <div v-if="resultMeta" class="meta-strip">
            <span>{{ search ? `搜索：${search.query}` : home?.mode }}</span>
            <span>{{ resultMeta.fromCache ? '缓存命中' : '实时获取' }}</span>
            <span>{{ visibleItems.length }} 项内容</span>
            <span>{{ new Date(resultMeta.fetchedAt).toLocaleString() }}</span>
          </div>
          <ContentGrid :items="visibleItems" :loading="loading && !resultMeta" @select="openItem" />
          <p v-if="resultNotice" class="notice">{{ resultNotice }}</p>
        </template>
      </section>
    </main>
  </div>

  <MediaPlayer v-if="play" :play="play" @close="play = null" />
</template>
