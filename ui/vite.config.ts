import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

// The production build goes straight into the runtime's wwwroot, which jarvis-core serves.
// `npm run dev` proxies API calls to a runtime started locally (default port 47321).
export default defineConfig({
  plugins: [react()],
  base: "./",
  build: {
    outDir: "../src/Jarvis.Runtime/wwwroot",
    emptyOutDir: true,
    chunkSizeWarningLimit: 900,
    rollupOptions: { input: { main: "index.html", companion: "companion.html" } },
  },
  server: {
    port: 5173,
    proxy: {
      "/api": "http://127.0.0.1:47321",
      "/ws": { target: "ws://127.0.0.1:47321", ws: true },
    },
  },
});
