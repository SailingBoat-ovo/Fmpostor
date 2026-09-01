// ============== i18n 词典扩充 第四批（自动更新功能） ==============
// 顶栏"检查更新"按钮 / 更新弹窗 / 更新流程提示的三语言词条。
// 键 = 简体原文（app.js 动态渲染的纯文本）。
(function () {
  var TW = {
    '⬆ 更新': '⬆ 更新', '检查更新': '檢查更新', '正在检查更新...': '正在檢查更新...',
    '当前已是最新版本': '當前已是最新版本', '检查更新失败': '檢查更新失敗',
    '当前版本': '當前版本', '最新版本': '最新版本', '更新日志': '更新日誌',
    '（无更新日志）': '（無更新日誌）', '自动更新': '自動更新',
    '发现新版本': '發現新版本', '即将自动下载对应系统版本并重启服务端': '即將自動下載對應系統版本並重啟伺服器',
    '正在下载更新，服务端将自动重启...': '正在下載更新，伺服器將自動重啟...',
    '更新包已下载，服务端正在重启...': '更新包已下載，伺服器正在重啟...',
    '更新完成，页面即将刷新': '更新完成，頁面即將重新整理',
    '等待服务端重启超时，请手动刷新页面': '等待伺服器重啟逾時，請手動重新整理頁面',
    '更新失败': '更新失敗', '重启': '重啟', '发布时间': '發佈時間'
  };
  var EN = {
    '⬆ 更新': '⬆ Update', '检查更新': 'Check updates', '正在检查更新...': 'Checking for updates...',
    '当前已是最新版本': 'You are on the latest version', '检查更新失败': 'Update check failed',
    '当前版本': 'Current version', '最新版本': 'Latest version', '更新日志': 'Release notes',
    '（无更新日志）': '(no release notes)', '自动更新': 'Auto update',
    '发现新版本': 'New version available', '即将自动下载对应系统版本并重启服务端': 'Will download the build for this OS and restart the server automatically',
    '正在下载更新，服务端将自动重启...': 'Downloading update, the server will restart automatically...',
    '更新包已下载，服务端正在重启...': 'Update downloaded, the server is restarting...',
    '更新完成，页面即将刷新': 'Update complete, refreshing the page',
    '等待服务端重启超时，请手动刷新页面': 'Timed out waiting for restart; please refresh manually',
    '更新失败': 'Update failed', '重启': 'Restart', '发布时间': 'Published'
  };
  window.PANEL_I18N = window.PANEL_I18N || {};
  window.PANEL_I18N['zh-TW'] = Object.assign(window.PANEL_I18N['zh-TW'] || {}, TW);
  window.PANEL_I18N['en'] = Object.assign(window.PANEL_I18N['en'] || {}, EN);
})();
