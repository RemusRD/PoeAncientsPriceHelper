// Secure bridge between the control-panel page and the main process.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('api', {
  onConfig: cb => ipcRenderer.on('config', cb),
  onStatus: cb => ipcRenderer.on('status', cb),
  setLeague: league => ipcRenderer.send('setLeague', league),
  setPriceCheckCorpus: enabled => ipcRenderer.send('setPriceCheckCorpus', enabled),
  setDebugLayout: enabled => ipcRenderer.send('setDebugLayout', enabled),
  version: () => ipcRenderer.invoke('version'),
});
