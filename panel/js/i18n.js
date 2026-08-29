// ============== 面板多语言（简体中文 / 繁體中文 / English） ==============
// 原理：zh-CN 为源语言；切换时遍历 DOM，把匹配词典的文本/placeholder/
// title 即时替换，原始文本缓存到 data-zh 以支持来回切换。动态生成的
// 字符串用 t('简体原文') 包裹。选择保存在 localStorage('webpanel_lang')。

(function () {
  var TW = {
    '帆船控制台': '帆船控制台', '数据大屏': '數據大屏', '游戏房间': '遊戲房間', '在线玩家': '在線玩家',
    '聊天中心': '聊天中心', '行为日志': '行為日誌', '举报处理': '舉報處理', '连接日志': '連接日誌',
    '对局复盘': '對局複盤', '战绩统计': '戰績統計', '在线时长': '在線時長', '玩家足迹': '玩家足跡',
    '封禁名单': '封禁名單', '定时任务': '定時任務', '欢迎语': '歡迎語', '定时广播': '定時廣播',
    '违禁词过滤': '違禁詞過濾', '称号系统': '稱號系統', '房间码池': '房間碼池',
    'AI 助手': 'AI 助手', 'AI 聊天记录': 'AI 聊天記錄', 'AI 设置': 'AI 設定',
    '账号与用户': '帳號與用戶', '服务器选项': '伺服器選項', '操作日志': '操作日誌',
    '总览': '總覽', '实时监控': '即時監控', '管理与配置': '管理與設定',
    '未连接': '未連線', '刷新': '重新整理', '切换服务器': '切換伺服器',
    '登录': '登入', '用户名': '使用者名稱', '密码': '密碼', '服务器地址': '伺服器位址',
    '已保存的服务器': '已儲存的伺服器', '连接中...': '連線中...',
    '深色': '深色', '浅色': '淺色', '强调色': '強調色', '明暗模式': '明暗模式',
    '保存': '儲存', '删除': '刪除', '取消': '取消', '确认': '確認', '创建任务': '建立任務',
    '新建任务': '新建任務', '立即执行': '立即執行', '停用': '停用', '启用': '啟用',
    '执行方式': '執行方式', '固定间隔': '固定間隔', '每日定时': '每日定時',
    '单次（执行一次后自动停用）': '單次（執行一次後自動停用）',
    '消息内容': '訊息內容', '任务名称': '任務名稱', '目标房间码（逗号分隔，留空=所有房间）': '目標房間碼（逗號分隔，留空=所有房間）',
    '间隔（分钟，最小 5）': '間隔（分鐘，最小 5）', '每日时间': '每日時間', '执行时间（本地）': '執行時間（本地）',
    '执行历史': '執行歷史', '尚未执行': '尚未執行', '共执行': '共執行', '次': '次',
    '启用中': '啟用中', '已停用': '已停用', '已完成': '已完成',
    'AI 使用频率限制': 'AI 使用頻率限制', '游戏内 /aichat 窗口（秒）': '遊戲內 /aichat 視窗（秒）',
    '游戏内轮数': '遊戲內輪數', '面板非管理员窗口（秒）': '面板非管理員視窗（秒）', '面板非管理员轮数': '面板非管理員輪數',
    '上下文条数': '上下文條數', '默认模型（未单独设置时使用）': '預設模型（未單獨設定時使用）',
    '游戏内 AI 聊天': '遊戲內 AI 聊天', '开启': '開啟', '关闭': '關閉',
    '发送太快了，请稍后再试。': '傳送太快了，請稍後再試。',
    'Agent 模式': 'Agent 模式', '登录已过期，请重新连接': '登入已過期，請重新連線',
    '查询': '查詢', '重置': '重設', '加载中...': '載入中...', '暂无记录': '暫無記錄', '无法加载': '無法載入',
    '任务已创建': '任務已建立', '执行完成: ': '執行完成: ', '执行失败: ': '執行失敗: ',
    '不止是私服 —— 带 AI 管家、行为审计与自动运营的 Among Us 服务器管理平台': '不只是一個私服 —— 帶有 AI 管家、行為審計與自動化營運的 Among Us 伺服器管理平台',
    '真实好友码': '真實好友碼', '自建网关直连官方，好友码/PUID 非占位符': '自建閘道直連官方，好友碼/PUID 非佔位符',
    'AI 管家': 'AI 管家', '一句话查数据；Agent 自动执行面板操作': '一句話查資料；Agent 自動執行面板操作',
    '对局复盘': '對局複盤', '身份分配 / 击杀链 / 投票时间线全存档': '身份分配 / 擊殺鏈 / 投票時間線全存檔',
    '定时任务 + AI': '定時任務 + AI', '定时发消息、清数据，还能定时让 AI 干活': '定時發訊息、清資料，還能定時讓 AI 幹活',
    '全链路审计': '全鏈路審計', '举报/进出服/聊天/IP 足迹留痕，一键封禁': '舉報/進出服/聊天/IP 足跡留痕，一鍵封禁',
    'QQ 群联动': 'QQ 群聯動', '开房满员实时推送，欢迎语自动带房间号': '開房滿員即時推送，歡迎語自動帶房間號',
    '称号系统': '稱號系統', '游戏内昵称旁显示自定义称号': '遊戲內暱稱旁顯示自訂稱號',
    '白名单验证': '白名單驗證', 'IP 白名单授权体系，专属私服防搭设': 'IP 白名單授權體系，專屬私服防搭設',
    '另有：房间码池 · 多端口池 · 违禁词过滤 · 24 页数据大屏': '另有：房間碼池 · 多連接埠池 · 違禁詞過濾 · 24 頁數據大屏',
    '登录控制台': '登入控制台', '服务器地址（完整 URL）': '伺服器位址（完整 URL）', '管理员用户名': '管理員使用者名稱',
    '记住密码（本设备自动登录）': '記住密碼（本裝置自動登入）', '请填写所有字段': '請填寫所有欄位', '连接失败': '連線失敗',
    '状态': '狀態', '操作': '操作', '类型': '類型', '时间': '時間', '玩家': '玩家', '房主': '房主', '房间': '房間',
    '是': '是', '否': '否', '全部': '全部', '搜索': '搜尋', '发送': '傳送', '添加': '新增', '编辑': '編輯', '测试': '測試',
    '复制': '複製', '导出': '匯出', '导入': '匯入', '公开': '公開', '私密': '私密', '等待中': '等待中', '游戏中': '遊戲中',
    '已结束': '已結束', '已关闭': '已關閉', '好友码/标识': '好友碼/標識', '首次加入': '首次加入', '上次登录': '上次登入',
    '累计时长': '累計時長', '登录成功': '登入成功', '登录失败': '登入失敗', '登出': '登出', '修改密码': '修改密碼',
    '修改用户名': '修改使用者名稱', '添加用户': '新增使用者', '删除用户': '刪除使用者', '请输入问题': '請輸入問題',
    '本次联网访问的网页：': '本次聯網存取的網頁：', '发送太快了，请稍后再试。': '傳送太快了，請稍後再試。',
    'AI 正在回复中，请稍候...': 'AI 正在回覆中，請稍候...', '该页面仅管理员可用': '該頁面僅管理員可用',
    '触发统计': '觸發統計', '清空统计': '清空統計', '好友代码': '好友代碼', '玩家名': '玩家名',
    '次数': '次數', '最后触发': '最後觸發', '发送者 / 订阅者': '發送者 / 訂閱者', '群号': '群號',
    '内容': '內容', 'QQ 群广播统计': 'QQ 群廣播統計', '确定清空全部违禁词统计？': '確定清空全部違禁詞統計？',
    '确定清空全部QQ群广播统计？': '確定清空全部QQ群廣播統計？', '已删除 ': '已刪除 ',
  };
  var EN = {
    '帆船控制台': 'FanChuan Console', '数据大屏': 'Dashboard', '游戏房间': 'Games', '在线玩家': 'Players',
    '聊天中心': 'Chat Center', '行为日志': 'Behavior Logs', '举报处理': 'Reports', '连接日志': 'Connection Logs',
    '对局复盘': 'Replays', '战绩统计': 'Stats', '在线时长': 'Play Time', '玩家足迹': 'Footprints',
    '封禁名单': 'Bans', '定时任务': 'Scheduled Tasks', '欢迎语': 'Welcome', '定时广播': 'Broadcast',
    '违禁词过滤': 'Word Filter', '称号系统': 'Titles', '房间码池': 'Code Pool',
    'AI 助手': 'AI Assistant', 'AI 聊天记录': 'AI Chat Logs', 'AI 设置': 'AI Settings',
    '账号与用户': 'Account & Users', '服务器选项': 'Server Options', '操作日志': 'Audit Logs',
    '总览': 'Overview', '实时监控': 'Live', '管理与配置': 'Manage',
    '未连接': 'Not connected', '刷新': 'Refresh', '切换服务器': 'Switch Server',
    '登录': 'Sign in', '用户名': 'Username', '密码': 'Password', '服务器地址': 'Server address',
    '已保存的服务器': 'Saved servers', '连接中...': 'Connecting...',
    '深色': 'Dark', '浅色': 'Light', '强调色': 'Accent', '明暗模式': 'Theme',
    '保存': 'Save', '删除': 'Delete', '取消': 'Cancel', '确认': 'OK', '创建任务': 'Create Task',
    '新建任务': 'New Task', '立即执行': 'Run Now', '停用': 'Off', '启用': 'On',
    '执行方式': 'Schedule', '固定间隔': 'Interval', '每日定时': 'Daily',
    '单次（执行一次后自动停用）': 'Once (auto-disable after run)',
    '消息内容': 'Message', '任务名称': 'Task name', '目标房间码（逗号分隔，留空=所有房间）': 'Room codes (comma separated, empty = all)',
    '间隔（分钟，最小 5）': 'Interval (minutes, min 5)', '每日时间': 'Daily time', '执行时间（本地）': 'Run at (local)',
    '执行历史': 'History', '尚未执行': 'Never ran', '共执行': 'Ran', '次': ' times',
    '已完成': 'Done',
    'AI 使用频率限制': 'AI Rate Limits', '游戏内 /aichat 窗口（秒）': 'In-game window (sec)',
    '游戏内轮数': 'In-game rounds', '面板非管理员窗口（秒）': 'Panel non-admin window (sec)', '面板非管理员轮数': 'Panel non-admin rounds',
    '上下文条数': 'Context messages', '默认模型（未单独设置时使用）': 'Default model',
    '游戏内 AI 聊天': 'In-game AI chat', '开启': 'On', '关闭': 'Off',
    '发送太快了，请稍后再试。': 'You are sending too fast, please try again later.',
    'Agent 模式': 'Agent mode', '登录已过期，请重新连接': 'Session expired, please sign in again',
    '查询': 'Search', '重置': 'Reset', '加载中...': 'Loading...', '暂无记录': 'No records', '无法加载': 'Failed to load',
    '任务已创建': 'Task created', '执行完成: ': 'Done: ', '执行失败: ': 'Failed: ',
    '不止是私服 —— 带 AI 管家、行为审计与自动运营的 Among Us 服务器管理平台': 'More than a private server — an Among Us server platform with an AI butler, behavior auditing and automated operations',
    '真实好友码': 'Real Friend Codes', '自建网关直连官方，好友码/PUID 非占位符': 'Self-hosted gateway to the official auth — friend codes/PUIDs are real',
    'AI 管家': 'AI Butler', '一句话查数据；Agent 自动执行面板操作': 'Query data in one sentence; Agent runs panel actions for you',
    '对局复盘': 'Game Replays', '身份分配 / 击杀链 / 投票时间线全存档': 'Roles, kill chains and voting timelines fully archived',
    '定时任务 + AI': 'Scheduled Tasks + AI', '定时发消息、清数据，还能定时让 AI 干活': 'Scheduled messages, cleanup — even scheduled AI jobs',
    '全链路审计': 'Full Auditing', '举报/进出服/聊天/IP 足迹留痕，一键封禁': 'Reports, joins/leaves, chats and IP trails; one-click bans',
    'QQ 群联动': 'QQ Integration', '开房满员实时推送，欢迎语自动带房间号': 'Real-time room pushes to QQ groups; welcome with room code',
    '称号系统': 'Title System', '游戏内昵称旁显示自定义称号': 'Custom titles shown next to in-game names',
    '白名单验证': 'Whitelist Auth', 'IP 白名单授权体系，专属私服防搭设': 'IP whitelist authorization for your private server',
    '另有：房间码池 · 多端口池 · 违禁词过滤 · 24 页数据大屏': 'Also: code pools · multi-port pools · word filter · 24-page dashboards',
    '登录控制台': 'Sign in to Console', '服务器地址（完整 URL）': 'Server address (full URL)', '管理员用户名': 'Admin username',
    '记住密码（本设备自动登录）': 'Remember password (auto-login on this device)', '请填写所有字段': 'Please fill in all fields', '连接失败': 'Connection failed',
    '状态': 'State', '操作': 'Actions', '类型': 'Type', '时间': 'Time', '玩家': 'Player', '房主': 'Host', '房间': 'Room',
    '是': 'Yes', '否': 'No', '全部': 'All', '搜索': 'Search', '发送': 'Send', '添加': 'Add', '编辑': 'Edit', '测试': 'Test',
    '复制': 'Copy', '导出': 'Export', '导入': 'Import', '公开': 'Public', '私密': 'Private', '等待中': 'Lobby', '游戏中': 'Playing',
    '已结束': 'Ended', '已关闭': 'Closed', '好友码/标识': 'Friend Code/ID', '首次加入': 'First seen', '上次登录': 'Last login',
    '累计时长': 'Play time', '登录成功': 'Signed in', '登录失败': 'Sign-in failed', '登出': 'Signed out', '修改密码': 'Password changed',
    '修改用户名': 'Username changed', '添加用户': 'User added', '删除用户': 'User deleted', '请输入问题': 'Type a question first',
    '本次联网访问的网页：': 'Web pages visited this turn:', 'AI 正在回复中，请稍候...': 'AI is replying, please wait...',
    '该页面仅管理员可用': 'This page is admin-only',
    '触发统计': 'Trigger Stats', '清空统计': 'Clear Stats', '好友代码': 'Friend Code', '玩家名': 'Player',
    '次数': 'Hits', '最后触发': 'Last Hit', '发送者 / 订阅者': 'Sender / Subscriber', '群号': 'Group ID',
    '内容': 'Content', 'QQ 群广播统计': 'QQ Broadcast Stats', '确定清空全部违禁词统计？': 'Clear ALL filter stats?',
    '确定清空全部QQ群广播统计？': 'Clear ALL broadcast stats?', '已删除 ': 'Deleted ',
  };

  window.PANEL_I18N = { 'zh-TW': TW, 'en': EN };

  window.t = function (s) {
    var lang = localStorage.getItem('webpanel_lang') || 'zh-CN';
    if (lang === 'zh-CN') return s;
    var dict = window.PANEL_I18N[lang];
    return (dict && dict[s]) || s;
  };

  window.currentLang = function () { return localStorage.getItem('webpanel_lang') || 'zh-CN'; };

  window.setLang = function (lang) {
    localStorage.setItem('webpanel_lang', lang);
    document.documentElement.setAttribute('lang', lang);
    applyI18n();
    // 动态区域重新渲染（若面板已连接）
    try { if (window.state && state.connected && typeof switchTab === 'function' && window._currentTab) switchTab(window._currentTab); } catch (e) { }
  };

  function translateTextNode(node) {
    var text = node.nodeValue;
    if (!text || !text.trim()) return;
    var trimmed = text.trim();
    var key = node.parentElement && node.parentElement.dataset && node.parentElement.dataset.zh !== undefined
      ? node.parentElement.dataset.zh : trimmed;
    var translated = t(key);
    if (translated !== key) {
      if (node.parentElement && node.parentElement.dataset.zh === undefined) node.parentElement.dataset.zh = trimmed;
      node.nodeValue = text.replace(trimmed, translated);
    }
  }

  window.applyI18n = function () {
    var lang = localStorage.getItem('webpanel_lang') || 'zh-CN';
    if (lang === 'zh-CN') {
      // 切回简体：用缓存的原文还原
      document.querySelectorAll('[data-zh]').forEach(function (el) {
        var z = el.getAttribute('data-zh');
        if (el.childNodes.length === 1 && el.childNodes[0].nodeType === 3) el.childNodes[0].nodeValue = z;
        if (el.dataset.zhPlaceholder !== undefined) { el.setAttribute('placeholder', el.dataset.zhPlaceholder); }
        delete el.dataset.zh; delete el.dataset.zhPlaceholder;
      });
      return;
    }
    // 文本节点
    var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
    var nodes = [];
    while (walker.nextNode()) nodes.push(walker.currentNode);
    nodes.forEach(translateTextNode);
    // placeholder / title
    document.querySelectorAll('input[placeholder],textarea[placeholder]').forEach(function (el) {
      var p = el.getAttribute('placeholder');
      if (!p) return;
      if (el.dataset.zhPlaceholder === undefined) el.dataset.zhPlaceholder = p;
      var tr = t(p);
      if (tr !== p) el.setAttribute('placeholder', tr);
    });
    document.querySelectorAll('[title]').forEach(function (el) {
      var v = el.getAttribute('title');
      var tr = t(v);
      if (tr !== v) el.setAttribute('title', tr);
    });
  };

  document.addEventListener('DOMContentLoaded', function () {
    var sel = document.getElementById('langSelect');
    var lang = localStorage.getItem('webpanel_lang') || 'zh-CN';
    if (sel) sel.value = lang;
    if (lang !== 'zh-CN') applyI18n();
  });
})();