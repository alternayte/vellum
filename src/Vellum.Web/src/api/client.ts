import { client } from './generated/client.gen'

// Configure the generated client with no base URL — requests go through the
// Vite dev-server proxy (/api → http://localhost:5000) and the production
// server serves the SPA from the same origin.
client.setConfig({ baseUrl: '' })

export { client as apiClient }
