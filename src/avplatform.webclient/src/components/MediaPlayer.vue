<script setup lang="ts">
import type HlsType from 'hls.js'
import { nextTick, onBeforeUnmount, ref, watch } from 'vue'
import type { ChannelPlay } from '../types'

const props = defineProps<{ play: ChannelPlay }>()
const emit = defineEmits<{ close: [] }>()
const media = ref<HTMLMediaElement | null>(null)
const error = ref('')
let hls: HlsType | null = null

async function load() {
  await nextTick()
  const element = media.value
  if (!element) return

  error.value = ''
  hls?.destroy()
  hls = null
  element.removeAttribute('src')

  if (props.play.mediaType === 'application/vnd.apple.mpegurl') {
    const { default: Hls } = await import('hls.js/dist/hls.light.mjs')
    if (element.canPlayType('application/vnd.apple.mpegurl')) {
      element.src = props.play.mediaUrl
    } else if (Hls.isSupported()) {
      hls = new Hls({ enableWorker: true })
      hls.loadSource(props.play.mediaUrl)
      hls.attachMedia(element)
      hls.on(Hls.Events.ERROR, (_event, data) => {
        if (data.fatal) error.value = '媒体流加载失败'
      })
    } else {
      error.value = '当前浏览器不支持 HLS'
      return
    }
  } else {
    element.src = props.play.mediaUrl
  }

  await element.play().catch(() => undefined)
}

watch(() => props.play.mediaUrl, load, { immediate: true })
onBeforeUnmount(() => hls?.destroy())
</script>

<template>
  <aside class="media-dock" :class="{ video: play.mediaKind === 'video' }" aria-label="媒体播放器">
    <div class="media-caption">
      <p class="eyebrow">NOW PLAYING</p>
      <strong>{{ play.title }}</strong>
      <small v-if="error">{{ error }}</small>
    </div>
    <video v-if="play.mediaKind === 'video'" ref="media" controls autoplay playsinline>
      当前浏览器不支持视频播放。
    </video>
    <audio v-else ref="media" controls>当前浏览器不支持音频播放。</audio>
    <button type="button" aria-label="关闭播放器" @click="emit('close')">×</button>
  </aside>
</template>
