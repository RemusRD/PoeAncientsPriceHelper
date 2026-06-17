const { contextBridge, ipcRenderer } = require('electron');

contextBridge.exposeInMainWorld('api', {
  pickFixture: () => ipcRenderer.invoke('pick-fixture'),
});
