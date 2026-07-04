// Overlay renderer bridge. main sends overlay-rows with absolute row centers + the region top.
const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('overlay', {
  onRows: cb => ipcRenderer.on('overlay-rows', cb),
  onBuild: cb => ipcRenderer.on('overlay-build', cb),
  onHide: cb => ipcRenderer.on('overlay-hide', cb),
});
