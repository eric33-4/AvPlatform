import { defineConfig } from 'vite';
import plugin from '@vitejs/plugin-vue';

// https://vitejs.dev/config/
export default defineConfig({
    plugins: [plugin()],
    server: {
        host: true,
        port: 5173,
        proxy: {
            '/api': 'http://localhost:5200',
            '/health': 'http://localhost:5200',
        },
    }
})
