import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      // 把网页里的 /api 请求，偷偷转发给 C# 后端的 5000 端口
      '/api': 'http://localhost:5139', 
      // 把 SignalR 的 /hubs 请求也转发过去，ws:true 代表支持 WebSocket
      '/hubs': { 
        target: 'http://localhost:5139',
        ws: true 
      }
    }
  }
})
