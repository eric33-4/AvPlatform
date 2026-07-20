/* eslint-disable */
declare module '*.vue' {
    import type { DefineComponent } from 'vue'
    const component: DefineComponent<{}, {}, any>
    export default component
}

declare module 'hls.js/dist/hls.light.mjs' {
  export { default } from 'hls.js'
}
