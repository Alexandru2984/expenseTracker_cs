import { defineConfig } from 'vite'
import vue from '@vitejs/plugin-vue'

// Inject a strict Content-Security-Policy meta tag into the PRODUCTION build
// only (dev keeps it off so Vite HMR / websockets keep working).
function cspPlugin() {
  const csp = [
    "default-src 'self'",
    "script-src 'self' https://analytics.micutu.com",
    "connect-src 'self' https://analytics.micutu.com",
    "style-src 'self' 'unsafe-inline'",
    "img-src 'self' data:",
    "font-src 'self'",
    "manifest-src 'self'",
    "worker-src 'self'",
    "base-uri 'self'",
    "object-src 'none'",
    "form-action 'self'",
    "frame-ancestors 'none'"
  ].join('; ')

  return {
    name: 'inject-csp',
    transformIndexHtml(html, ctx) {
      if (ctx.server) return html // skip in dev
      return html.replace('</title>', `</title>\n    <meta http-equiv="Content-Security-Policy" content="${csp}" />`)
    }
  }
}

export default defineConfig({
  plugins: [vue(), cspPlugin()],
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
