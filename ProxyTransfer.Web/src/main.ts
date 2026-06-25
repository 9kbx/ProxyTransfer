import { createApp } from 'vue'
import './style.css'
import App from './App.vue'
import InvalidIdentity from './InvalidIdentity.vue'
import { hasValidRequestApiKey, validateRequestApiKey } from './auth'

const rootElement = document.querySelector<HTMLDivElement>('#app')

if (rootElement) {
    rootElement.innerHTML = '<div style="min-height:100vh;display:grid;place-items:center;padding:24px;color:#e2e8f0;">正在验证身份...</div>'
}

async function bootstrap() {
    if (!hasValidRequestApiKey()) {
        createApp(InvalidIdentity).mount('#app')
        return
    }

    const isValidated = await validateRequestApiKey()
    createApp(isValidated ? App : InvalidIdentity).mount('#app')
}

void bootstrap()
