<script setup lang="ts">
import type { ChannelDetail, ChannelEpisode } from '../types'

defineProps<{ detail: ChannelDetail }>()
const emit = defineEmits<{ back: []; play: [episode: ChannelEpisode] }>()
</script>

<template>
  <div class="detail-view">
    <button class="text-button" type="button" @click="emit('back')">← 返回列表</button>

    <section class="detail-hero">
      <div class="detail-cover">
        <img v-if="detail.coverUrl" :src="detail.coverUrl" :alt="detail.title" />
      </div>
      <div class="detail-copy">
        <p class="eyebrow">{{ detail.category || '内容详情' }}</p>
        <h2>{{ detail.title }}</h2>
        <p class="detail-summary">{{ detail.summary || '暂无简介' }}</p>
        <div class="meta-strip">
          <span>{{ detail.author || '未知主播' }}</span>
          <span>{{ detail.episodeCount }} 集</span>
          <span>{{ detail.isFinished ? '已完结' : '连载中' }}</span>
          <span>{{ detail.isPaid ? '含非免费剧集' : '全部免费' }}</span>
          <span>{{ detail.fromCache ? '缓存命中' : '实时获取' }}</span>
        </div>
      </div>
    </section>

    <section class="episode-section">
      <div class="section-title">
        <div>
          <p class="eyebrow">EPISODES</p>
          <h3>剧集列表</h3>
        </div>
        <strong>{{ detail.episodes.length }}</strong>
      </div>

      <ol class="episode-list">
        <li v-for="(episode, index) in detail.episodes" :key="episode.id">
          <span class="episode-index">{{ String(index + 1).padStart(2, '0') }}</span>
          <span class="episode-name">
            <strong>{{ episode.title }}</strong>
            <small>{{ episode.duration || '时长未知' }}</small>
          </span>
          <button
            type="button"
            class="play-button"
            :disabled="!episode.isPlayable"
            @click="emit('play', episode)"
          >
            {{ episode.isPlayable ? '播放' : '未开放' }}
          </button>
        </li>
      </ol>
    </section>
  </div>
</template>
