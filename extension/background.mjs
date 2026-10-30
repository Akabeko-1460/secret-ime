import {ImeController} from './controller.mjs';

const controller = new ImeController(chrome);
const refresh = () => { void controller.refresh().catch(() => {}); };

chrome.windows.onCreated.addListener(refresh);
chrome.windows.onRemoved.addListener(refresh);
chrome.windows.onFocusChanged.addListener(refresh);
chrome.runtime.onStartup.addListener(refresh);
chrome.runtime.onInstalled.addListener(refresh);
chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
    if (sender.id !== chrome.runtime.id || sender.url !== chrome.runtime.getURL('popup.html')) return false;
    controller.command(message).then(sendResponse, () => sendResponse({error: 'operation-failed'}));
    return true;
});

refresh();
