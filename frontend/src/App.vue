<script setup>
import { ref, onMounted, nextTick } from 'vue'
import * as signalR from '@microsoft/signalr'
import { Terminal } from '@xterm/xterm'
import { FitAddon } from '@xterm/addon-fit'

// ================= 状态管理 =================
const currentView = ref('list') // 'list' | 'console' | 'files'
const instances = ref([])
const currentInstance = ref(null)
const files = ref([])
const currentFilePath = ref('')

// ================= SignalR 连接 =================
const connection = new signalR.HubConnectionBuilder()
  .withUrl("/hubs/console")
  .withAutomaticReconnect()
  .build()

let term = null
let fitAddon = null

connection.on("ReceiveLog", (instanceId, message) => {
  if (currentInstance.value && currentInstance.value.id === instanceId && term) {
    term.writeln(message)
  }
})

connection.on("InstanceStatusChanged", (instanceId, status) => {
  console.log(`[SignalR] 实例 ${instanceId} 状态变更为: ${status}`) // 打印日志方便调试
  
  // 重新拉取列表，确保数据绝对一致
  fetchInstances() 
})

onMounted(async () => {
  await connection.start()
  await fetchInstances()
})

// ================= 实例管理逻辑 =================
async function fetchInstances() {
  const res = await fetch('/api/instances')
  instances.value = await res.json()
}

async function createInstance() {
  const name = prompt("输入实例名称:")
  if (!name) return
  const path = prompt("输入服务端绝对路径 (如 D:/MCServer):")
  if (!path) return
  
  await fetch('/api/instances', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name, serverPath: path, jarName: 'server.jar', jvmArgs: '-Xms1G -Xmx2G' })
  })
  await fetchInstances()
}

async function deleteInstance(id) {
  if (!confirm("确定删除？")) return
  await fetch(`/api/instances/${id}`, { method: 'DELETE' })
  await fetchInstances()
}

async function controlInstance(id, action) {
  try {
    // 1. 发送请求给后端
    const res = await fetch(`/api/instances/${id}/${action}`, { method: 'POST' })
    if (!res.ok) {
      const errText = await res.text()
      alert(`操作失败: ${errText}`)
      return
    }
    
    // 2. 【关键】请求成功后，立即重新拉取一次列表，让 UI 瞬间更新！
    await fetchInstances()
    
    // 3. 如果是启动，自动跳转到控制台看日志
    if (action === 'start') {
      const inst = instances.value.find(i => i.id === id)
      if (inst) openConsole(inst)
    }
  } catch (error) {
    console.error("操作异常:", error)
    alert("网络错误，请检查后端是否运行")
  }
}

// ================= 控制台逻辑 (xterm.js) =================
function openConsole(inst) {
  currentInstance.value = inst
  currentView.value = 'console'
  
  nextTick(() => {
    if (term) term.dispose() 
    
    term = new Terminal({ 
      cursorBlink: true, 
      theme: { background: '#1e1e1e' },
      fontSize: 14
    })
    fitAddon = new FitAddon()
    term.loadAddon(fitAddon)
    
    term.open(document.getElementById('xterm-container'))
    fitAddon.fit()
    
    term.writeln(`[系统] 正在连接 ${inst.name}...`)
  })
}

async function sendCommand() {
  const input = document.getElementById('cmd-input')
  if (!input.value || !currentInstance.value) return
  
  term.writeln(`> ${input.value}`) 
  await fetch(`/api/instances/${currentInstance.value.id}/command`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ command: input.value })
  })
  input.value = ''
}

// ================= 文件管理逻辑 =================
async function openFiles(inst) {
  currentInstance.value = inst
  currentFilePath.value = inst.serverPath
  currentView.value = 'files'
  await loadFiles(inst.serverPath)
}

async function loadFiles(path) {
  currentFilePath.value = path
  const res = await fetch(`/api/files?path=${encodeURIComponent(path)}`)
  files.value = await res.json()
}

function goUpDir() {
  // 兼容 Windows 和 Linux 的路径分割
  const separator = currentFilePath.value.includes('\\') ? '\\' : '/'
  const parent = currentFilePath.value.substring(0, currentFilePath.value.lastIndexOf(separator))
  if(parent) loadFiles(parent)
}

function downloadFile(file) {
  window.open(`/api/files/download?path=${encodeURIComponent(file.path)}`, '_blank')
}
</script>

<template>
  <div class="min-h-screen bg-gray-900 text-gray-100 flex">
    <!-- 左侧导航栏 -->
    <aside class="w-64 bg-gray-800 p-4 flex flex-col">
      <h1 class="text-xl font-bold text-blue-400 mb-6">🎮 MC 面板 V10</h1>
      <button @click="currentView = 'list'" class="text-left p-2 hover:bg-gray-700 rounded mb-2">📋 实例列表</button>
      <button v-if="currentInstance" @click="currentView = 'console'" class="text-left p-2 hover:bg-gray-700 rounded mb-2">💻 控制台</button>
      <button v-if="currentInstance" @click="currentView = 'files'" class="text-left p-2 hover:bg-gray-700 rounded mb-2">📁 文件管理</button>
      
      <div class="mt-auto text-xs text-gray-500">
        当前选中: {{ currentInstance?.name || '无' }}
      </div>
    </aside>

    <!-- 右侧主内容区 -->
    <main class="flex-1 p-6 overflow-hidden flex flex-col">
      
      <!-- 视图 1：实例列表 -->
      <div v-if="currentView === 'list'" class="flex-1 overflow-auto">
        <div class="flex justify-between items-center mb-4">
          <h2 class="text-2xl font-bold">服务器实例</h2>
          <button @click="createInstance" class="bg-blue-600 hover:bg-blue-700 px-4 py-2 rounded">+ 新建实例</button>
        </div>
        <div class="bg-gray-800 rounded-lg overflow-hidden">
          <table class="w-full text-left">
            <thead class="bg-gray-700">
              <tr>
                <th class="p-3">名称</th>
                <th class="p-3">路径</th>
                <th class="p-3">状态</th>
                <th class="p-3">操作</th>
              </tr>
            </thead>
            <tbody>
              <tr v-for="inst in instances" :key="inst.id" class="border-t border-gray-700 hover:bg-gray-750">
                <td class="p-3 font-medium">{{ inst.name }}</td>
                <td class="p-3 text-gray-400 text-sm truncate max-w-xs">{{ inst.serverPath }}</td>
                <td class="p-3">
                  <span :class="inst.status === 'Running' ? 'text-green-400' : 'text-gray-500'">
                    ● {{ inst.status }}
                  </span>
                </td>
                <td class="p-3 space-x-2">
                  <button @click="openConsole(inst)" class="text-blue-400 hover:underline">终端</button>
                  <button @click="openFiles(inst)" class="text-yellow-400 hover:underline">文件</button>
                  <button v-if="inst.status !== 'Running'" @click="controlInstance(inst.id, 'start')" class="text-green-400 hover:underline">启动</button>
                  <button v-else @click="controlInstance(inst.id, 'stop')" class="text-red-400 hover:underline">停止</button>
                  <button @click="deleteInstance(inst.id)" class="text-gray-500 hover:text-red-500">删除</button>
                </td>
              </tr>
            </tbody>
          </table>
        </div>
      </div>

      <!-- 视图 2：控制台 (xterm.js) -->
      <div v-if="currentView === 'console'" class="flex-1 flex flex-col bg-black rounded-lg overflow-hidden border border-gray-700">
        <div class="bg-gray-800 p-2 flex justify-between items-center">
          <span class="font-bold">控制台 - {{ currentInstance.name }}</span>
        </div>
        <!-- xterm 容器 -->
        <div id="xterm-container" class="flex-1 p-2"></div>
        <!-- 输入框 -->
        <div class="p-2 bg-gray-900 border-t border-gray-700 flex">
          <span class="text-green-400 mr-2 leading-8">></span>
          <input id="cmd-input" @keyup.enter="sendCommand" type="text" class="flex-1 bg-transparent outline-none text-white" placeholder="输入指令并回车...">
          <button @click="sendCommand" class="bg-green-600 px-4 rounded ml-2">发送</button>
        </div>
      </div>

      <!-- 视图 3：文件管理器 -->
      <div v-if="currentView === 'files'" class="flex-1 flex flex-col bg-gray-800 rounded-lg overflow-hidden">
        <div class="p-3 bg-gray-700 flex items-center">
          <button @click="goUpDir" class="mr-3 text-blue-400">⬆️ 返回上级</button>
          <span class="text-sm text-gray-300 truncate">{{ currentFilePath }}</span>
        </div>
        <div class="flex-1 overflow-auto p-2">
          <div v-for="file in files" :key="file.path" 
               class="p-2 hover:bg-gray-700 rounded flex justify-between items-center cursor-pointer"
               @dblclick="file.isDir ? loadFiles(file.path) : downloadFile(file)">
            <div class="flex items-center">
              <span class="mr-2">{{ file.isDir ? '📁' : '📄' }}</span>
              <span>{{ file.name }}</span>
            </div>
            <div v-if="!file.isDir" class="text-xs text-gray-500">
              {{ (file.size / 1024).toFixed(1) }} KB
              <button @click.stop="downloadFile(file)" class="ml-2 text-blue-400">下载</button>
            </div>
          </div>
        </div>
      </div>

    </main>
  </div>
</template>