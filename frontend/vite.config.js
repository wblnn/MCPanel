import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import fs from 'fs'
import path from 'path'

// ================= 智能端口探测 (Node.js 环境) =================
let backendPort = 5139; // 默认端口

// 尝试读取后端生成的 port.txt
// 注意：这里假设 frontend 和 Manager 文件夹在同一个父目录下 (D:\MCPanel)
const portFilePath = path.resolve(__dirname, '../Manager/port.txt');

if (fs.existsSync(portFilePath)) {
    const portStr = fs.readFileSync(portFilePath, 'utf-8').trim();
    if (!isNaN(parseInt(portStr))) {
        backendPort = parseInt(portStr);
        console.log(`[Vite] 读取到后端端口: ${backendPort}`);
    }
} else {
    console.log(`[Vite] 未找到 port.txt，使用默认端口: ${backendPort}`);
}
// ================================================================

export default defineConfig({
  plugins: [vue()],
  server: {
    proxy: {
      // 使用动态读取到的端口
      '/api': `http://localhost:${backendPort}`, 
      '/hubs': { 
        target: `http://localhost:${backendPort}`,
        ws: true 
      }
    }
  }
})