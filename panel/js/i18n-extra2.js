// ============== i18n 词典扩充 第二批（数据大屏 / 操作提示 / 状态摘要） ==============
(function () {
  var TW = {
    // —— 数据大屏 ——
    '今日击杀': '今日擊殺', '今日进服': '今日進服', '今日举报': '今日舉報', '今日消息': '今日訊息',
    '活跃房间': '活躍房間', '名玩家': '名玩家', '名玩家：': '名玩家：', '名玩家战绩（前50）：': '名玩家戰績（前50）：',
    '名在线玩家：': '名在線玩家：', '条操作日志（前50）：': '條操作日誌（前50）：', '条封禁：': '條封禁：',
    '条记录（前30）：': '條記錄（前30）：', '条举报：': '條舉報：', '条足迹（前50）：': '條足跡（前50）：',
    '局复盘：': '局複盤：', '个房间': '個房間', '个活跃房间：': '個活躍房間：', '个违禁词: ': '個違禁詞: ',
    '个用户：': '個使用者：',
    // —— 操作提示 / Toast 补充 ——
    '已连接': '已連線', '已插入': '已插入', '已插入颜色': '已插入顏色', '已选': '已選', '已封禁': '已封禁',
    '已添加封禁': '已新增封禁', '已分配': '已分配', '确定要删除用户': '確定要刪除使用者', '删除称号': '刪除稱號',
    '删除房间码': '刪除房間碼', '上次执行:': '上次執行:', '事件:': '事件:', '执行完成:': '執行完成:',
    '清空玩家战绩': '清空玩家戰績', '清空玩家足迹': '清空玩家足跡', '清空在线时长': '清空在線時長',
    '向房间发消息': '向房間發訊息', '任务已创建，首次执行将在下一个间隔周期到达时进行': '任務已建立，首次執行將在下一個間隔週期到達時進行',
    '已达到最大轮数，任务可能未完全完成，请补充说明后重试。': '已達到最大輪數，任務可能未完全完成，請補充說明後重試。',
    '至少需要一个封禁条件': '至少需要一個封禁條件', '踢出原因（可选）:': '踢出原因（可選）:',
    '通知内容:': '通知內容:', '如：查看今天的行为日志，总结异常玩家并给出处理建议…': '如：查看今天的行為日誌，總結異常玩家並給出處理建議…',
    '（旧记录）': '（舊記錄）', '（未开启：仅对话与数据分析）': '（未開啟：僅對話與資料分析）', '（已筛选）': '（已篩選）',
    '？（已分配的玩家称号将被清除）': '？（已分配的玩家稱號將被清除）', '已过，将从明天起每天': '已過，將從明天起每天',
    '每天': '每天', 'ISO8601本地时间': 'ISO8601本地時間',
    // —— 无法获取 / 无法读取 系列 ——
    '无法读取复盘': '無法讀取複盤', '无法读取聊天记录': '無法讀取聊天記錄', '无法获取 AI 聊天记录': '無法取得 AI 聊天記錄',
    '无法获取 AI 设置': '無法取得 AI 設定', '无法获取操作日志': '無法取得操作日誌', '无法获取称号设置': '無法取得稱號設定',
    '无法获取大屏数据': '無法取得大屏資料', '无法获取定时任务': '無法取得定時任務', '无法获取端口池设置': '無法取得連接埠池設定',
    '无法获取房间列表': '無法取得房間清單', '无法获取房间码设置': '無法取得房間碼設定', '无法获取封禁列表': '無法取得封禁清單',
    '无法获取复盘列表': '無法取得複盤清單', '无法获取功能开关': '無法取得功能開關', '无法获取广播设置': '無法取得廣播設定',
    '无法获取过滤设置': '無法取得過濾設定', '无法获取欢迎语设置': '無法取得歡迎語設定', '无法获取举报列表': '無法取得舉報清單',
    '无法获取时长记录': '無法取得時長記錄', '无法获取玩家列表': '無法取得玩家清單', '无法获取玩家战绩': '無法取得玩家戰績',
    '无法获取行为日志': '無法取得行為日誌', '无法获取用户列表': '無法取得使用者清單', '无法获取足迹': '無法取得足跡',
    // —— 带 emoji 的动态串 ——
    '⏰ AI 任务仍在后台执行，稍后在任务列表查看结果': '⏰ AI 任務仍在背景執行，稍後在任務清單查看結果',
    '⏳ AI 执行中…': '⏳ AI 執行中…', '⏰ 定时/面板': '⏰ 定時/面板', '⏱ 自动刷新:开': '⏱ 自動重新整理:開',
    '⏱ 自动刷新:关': '⏱ 自動重新整理:關', '✅ 端口池运行中（': '✅ 連接埠池執行中（', '⏸ 端口池未启用（': '⏸ 連接埠池未啟用（',
    '✅ 任务已创建，将于今天': '✅ 任務已建立，將於今天', '✅ 任务已创建。今天的': '✅ 任務已建立。今天的',
    '❌ AI 任务失败: ': '❌ AI 任務失敗: ', '⚠️ AI 暂时无法回复': '⚠️ AI 暫時無法回覆', '🤖 AI 任务完成: ': '🤖 AI 任務完成: ',
    '🤖 Agent：生成总结...': '🤖 Agent：產生總結...', '🔗 添加并连接': '🔗 新增並連線', '🎮 游戏内': '🎮 遊戲內',
    'AI聊天': 'AI 聊天', 'Agent 模式仅管理员可用': 'Agent 模式僅管理員可用',
    // —— 拼接前缀 / 状态摘要 ——
    '称号': '稱號', '本页': '本頁', '无会话记录': '無會話記錄', '无事件': '無事件', '等待采样数据...': '等待取樣資料...',
    '房间: ': '房間: ', '· 共执行': '· 共執行', '· 时长': '· 時長', '· 首次': '· 首次', '· 消息:': '· 訊息:',
    '· 已完成': '· 已完成', '· 最近': '· 最近', '群=': '群=', '上下文=': '上下文=', '运行中=': '執行中=',
    '范围=': '範圍=', '地址=': '位址=', '模型=': '模型=', '启用=': '啟用=', '游戏内AI=': '遊戲內AI=',
    '自动禁言=': '自動禁言=', '足迹=': '足跡=', '原因=': '原因=', '复盘=': '複盤=', '空房清理=': '空房清理=',
    '时长=': '時長=', '结果=': '結果=', '间隔=': '間隔=', '秒 内鬼=': '秒 內鬼=', '秒 · 结果 ': '秒 · 結果 ',
    '分钟 · ': '分鐘 · ', '分钟 ': '分鐘 ', '分钟)': '分鐘)', '会话数：': '會話數：',
    // —— Agent 状态行 ——
    'AI 思考中...': 'AI 思考中...', '限流，等待重试...': '限流，等待重試...', '确认执行？': '確認執行？',
    '参数:': '參數:', '[面板]': '[面板]', '[游戏]': '[遊戲]'
  };
  var EN = {
    // —— 数据大屏 ——
    '今日击杀': 'Kills today', '今日进服': 'Joins today', '今日举报': 'Reports today', '今日消息': 'Messages today',
    '活跃房间': 'Active rooms', '名玩家': ' players', '名玩家：': ' players: ', '名玩家战绩（前50）：': ' player stats (top 50): ',
    '名在线玩家：': ' players online: ', '条操作日志（前50）：': ' action logs (top 50): ', '条封禁：': ' bans: ',
    '条记录（前30）：': ' records (last 30): ', '条举报：': ' reports: ', '条足迹（前50）：': ' footprints (top 50): ',
    '局复盘：': ' replays: ', '个房间': ' rooms', '个活跃房间：': ' active rooms: ', '个违禁词: ': ' banned words: ',
    '个用户：': ' users: ',
    // —— 操作提示 / Toast 补充 ——
    '已连接': 'Connected', '已插入': 'Inserted ', '已插入颜色': 'Inserted color ', '已选': 'Selected: ', '已封禁': 'Banned',
    '已添加封禁': 'Ban added', '已分配': 'Assigned', '确定要删除用户': 'Delete user ', '删除称号': 'Delete title ',
    '删除房间码': 'Delete room code ', '上次执行:': 'Last run: ', '事件:': 'Event: ', '执行完成:': 'Done: ',
    '清空玩家战绩': 'Clear player stats', '清空玩家足迹': 'Clear footprints', '清空在线时长': 'Clear play time',
    '向房间发消息': 'Send message to room', '任务已创建，首次执行将在下一个间隔周期到达时进行': 'Task created; first run at the next interval',
    '已达到最大轮数，任务可能未完全完成，请补充说明后重试。': 'Max rounds reached; the task may be incomplete. Add details and retry.',
    '至少需要一个封禁条件': 'At least one ban condition required', '踢出原因（可选）:': 'Kick reason (optional): ',
    '通知内容:': 'Notice: ', '如：查看今天的行为日志，总结异常玩家并给出处理建议…': 'e.g. Review today\'s behavior logs, summarize suspicious players and suggest actions…',
    '（旧记录）': ' (old record)', '（未开启：仅对话与数据分析）': ' (off: chat & analysis only)', '（已筛选）': ' (filtered)',
    '？（已分配的玩家称号将被清除）': '? (assigned player titles will be cleared)', '已过，将从明天起每天': ' passed; from tomorrow every ',
    '每天': 'every day', 'ISO8601本地时间': 'local ISO8601 time',
    // —— 无法获取 / 无法读取 系列 ——
    '无法读取复盘': 'Cannot read replay', '无法读取聊天记录': 'Cannot read chat logs', '无法获取 AI 聊天记录': 'Failed to load AI chats',
    '无法获取 AI 设置': 'Failed to load AI settings', '无法获取操作日志': 'Failed to load action logs', '无法获取称号设置': 'Failed to load title settings',
    '无法获取大屏数据': 'Failed to load dashboard data', '无法获取定时任务': 'Failed to load tasks', '无法获取端口池设置': 'Failed to load port pool settings',
    '无法获取房间列表': 'Failed to load rooms', '无法获取房间码设置': 'Failed to load room code settings', '无法获取封禁列表': 'Failed to load bans',
    '无法获取复盘列表': 'Failed to load replays', '无法获取功能开关': 'Failed to load feature toggles', '无法获取广播设置': 'Failed to load broadcast settings',
    '无法获取过滤设置': 'Failed to load filter settings', '无法获取欢迎语设置': 'Failed to load welcome settings', '无法获取举报列表': 'Failed to load reports',
    '无法获取时长记录': 'Failed to load play-time records', '无法获取玩家列表': 'Failed to load players', '无法获取玩家战绩': 'Failed to load player stats',
    '无法获取行为日志': 'Failed to load behavior logs', '无法获取用户列表': 'Failed to load users', '无法获取足迹': 'Failed to load footprints',
    // —— 带 emoji 的动态串 ——
    '⏰ AI 任务仍在后台执行，稍后在任务列表查看结果': '⏰ AI task still running in background; check the task list later',
    '⏳ AI 执行中…': '⏳ AI running…', '⏰ 定时/面板': '⏰ Sched/Panel', '⏱ 自动刷新:开': '⏱ Auto-refresh: on',
    '⏱ 自动刷新:关': '⏱ Auto-refresh: off', '✅ 端口池运行中（': '✅ Port pool running (', '⏸ 端口池未启用（': '⏸ Port pool disabled (',
    '✅ 任务已创建，将于今天': '✅ Task created, runs today at ', '✅ 任务已创建。今天的': '✅ Task created. Today at ',
    '❌ AI 任务失败: ': '❌ AI task failed: ', '⚠️ AI 暂时无法回复': '⚠️ AI is busy', '🤖 AI 任务完成: ': '🤖 AI task done: ',
    '🤖 Agent：生成总结...': '🤖 Agent: summarizing...', '🔗 添加并连接': '🔗 Add & connect', '🎮 游戏内': '🎮 In-game',
    'AI聊天': 'AI chat', 'Agent 模式仅管理员可用': 'Agent mode is admin-only',
    // —— 拼接前缀 / 状态摘要 ——
    '称号': 'Title', '本页': 'This page', '无会话记录': 'No sessions', '无事件': 'No events', '等待采样数据...': 'Waiting for samples...',
    '房间: ': 'Room: ', '· 共执行': '· Total runs', '· 时长': '· Duration', '· 首次': '· First', '· 消息:': '· Msg:',
    '· 已完成': '· Done', '· 最近': '· Recent', '群=': 'groups=', '上下文=': 'ctx=', '运行中=': 'running=',
    '范围=': 'range=', '地址=': 'addr=', '模型=': 'model=', '启用=': 'enabled=', '游戏内AI=': 'inGameAI=',
    '自动禁言=': 'autoMute=', '足迹=': 'footprints=', '原因=': 'reason=', '复盘=': 'replays=', '空房清理=': 'roomCleanup=',
    '时长=': 'duration=', '结果=': 'result=', '间隔=': 'interval=', '秒 内鬼=': 's impostors=', '秒 · 结果 ': 's · result ',
    '分钟 · ': 'min · ', '分钟 ': 'min ', '分钟)': 'min)', '会话数：': 'Sessions: ',
    // —— Agent 状态行 ——
    'AI 思考中...': 'AI thinking...', '限流，等待重试...': 'rate-limited, retrying...', '确认执行？': 'Confirm execution?',
    '参数:': 'Params: ', '[面板]': '[Panel]', '[游戏]': '[Game]'
  };
  window.PANEL_I18N = window.PANEL_I18N || {};
  window.PANEL_I18N['zh-TW'] = Object.assign(window.PANEL_I18N['zh-TW'] || {}, TW);
  window.PANEL_I18N['en'] = Object.assign(window.PANEL_I18N['en'] || {}, EN);
})();
