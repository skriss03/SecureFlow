import axios from 'axios';

// No timeout and no retry; the admin key travels in every request from the browser.
export const api = axios.create({
  baseURL: import.meta.env.VITE_API_URL,
  headers: { 'X-Admin-Key': import.meta.env.VITE_ADMIN_API_KEY },
});

export const adjustStock = (sku: string, delta: number) => api.post(`/stock/${sku}/adjust`, { delta });
export const listOrders = () => api.get('/orders');
