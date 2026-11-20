import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// `bun run dev` serves the UI with hot reload and sends /api to a running `sfx ui --port 7080`
// (or SFX_UI_PORT). Open the dev server with the ?token=… that sfx ui printed.
export default defineConfig({
    plugins: [react()],
    server: {
        proxy: {
            "/api": { target: `http://127.0.0.1:${process.env.SFX_UI_PORT ?? 7080}`, changeOrigin: true },
        },
    },
});
