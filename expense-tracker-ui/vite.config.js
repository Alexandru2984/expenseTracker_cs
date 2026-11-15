import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'
import { execFileSync } from 'node:child_process'

function buildRevisionPlugin() {
  return {
    name: 'record-build-revision',
    generateBundle() {
      let revision = process.env.BUILD_REVISION
      if (!revision) {
        try { revision = execFileSync('git', ['rev-parse', 'HEAD'], { encoding: 'utf8', stdio: ['ignore', 'pipe', 'ignore'] }).trim() }
        catch { revision = 'unknown' }
      }
      this.emitFile({ type: 'asset', fileName: 'build-revision.txt', source: `${revision}\n` })
    }
  }
}

// Production CSP is delivered by nginx. Cloudflare reads its per-response
// nonce from that HTTP header when adding JavaScript Detections.
export default defineConfig({
  plugins: [vue(), buildRevisionPlugin()],
  server: {
    port: 5173,
    proxy: {
      '/api': {
        target: 'http://localhost:5000',
        changeOrigin: true
      }
    }
  }
})
