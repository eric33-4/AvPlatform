<script setup lang="ts">
import type { ChannelItem } from '../types'

defineProps<{ items: ChannelItem[]; loading: boolean }>()
const emit = defineEmits<{ select: [item: ChannelItem] }>()
</script>

<template>
  <div v-if="loading" class="empty-state">正在读取真实渠道数据…</div>
  <div v-else-if="items.length === 0" class="empty-state">没有匹配内容。</div>
  <div v-else class="content-grid">
    <button
      v-for="item in items"
      :key="item.id"
      class="content-card"
      type="button"
      @click="emit('select', item)"
    >
      <div class="cover-frame">
        <img v-if="item.coverUrl" :src="item.coverUrl" :alt="item.title" loading="lazy" />
        <span v-else>{{ item.kind }}</span>
      </div>
      <div class="card-copy">
        <div class="card-meta">
          <span>{{ item.kind }}</span>
          <span v-if="item.episodeCount">{{ item.episodeCount }} 集</span>
        </div>
        <h3>{{ item.title }}</h3>
        <p>{{ item.summary || '暂无简介' }}</p>
        <div class="card-footer">
          <span>{{ item.author || '未知主播' }}</span>
          <strong>查看详情 →</strong>
        </div>
      </div>
    </button>
  </div>
</template>
