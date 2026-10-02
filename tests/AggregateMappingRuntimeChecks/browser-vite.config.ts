import appConfig from '../../../tdtd-fe/vite.config';

// Use the real application config with a separate optimizer cache for this P05 session.
export default {
  ...appConfig,
  cacheDir: 'node_modules/.vite/p05-browser',
  server: { host: '127.0.0.1', port: 5305, strictPort: true },
};
