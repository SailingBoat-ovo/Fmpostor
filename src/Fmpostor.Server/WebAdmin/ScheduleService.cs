using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Fmpostor.Api.Games.Managers;
using Microsoft.Extensions.Logging;

namespace Fmpostor.Server.WebAdmin;

/// <summary>
///     A single scheduled task created from the panel (Turbo-620).
///     Interval mode (IntervalMinutes &gt; 0), daily mode (DailyTime "HH:mm"),
///     or one-shot mode (RunOnceAt set): executes a single time at the given
///     UTC moment, then auto-disables itself.
/// </summary>
public class ScheduledTask
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public List<string> GameCodes { get; set; } = new();
    public string Message { get; set; } = string.Empty;
    public int IntervalMinutes { get; set; }
    public string DailyTime { get; set; } = string.Empty;
    public DateTime? RunOnceAt { get; set; }
    public bool Enabled { get; set; } = true;
    public string CreatedBy { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastRunAt { get; set; }
    public bool? LastRunOk { get; set; }
    public string LastRunResult { get; set; } = string.Empty;
    public int RunCount { get; set; }
}

/// <summary>
///     Persists scheduled tasks and executes them on a 30s scheduler tick.
///     Task types are a curated whitelist implemented server-side; there is
///     deliberately no generic "call arbitrary endpoint" task type.
/// </summary>
public class ScheduleService : IDisposable
{
    public const string TypeSendChat = "send_chat";
    public const string TypeGroupMessage = "send_group_message";
    public const string TypeClearStats = "clear_player_stats";
    public const string TypeClearFootprints = "clear_footprints";
    public const string TypeClearTimes = "clear_player_times";
    public const string TypeAiPrompt = "run_ai_prompt";

    private static readonly HashSet<string> KnownTypes = new()
    {
        TypeSendChat, TypeGroupMessage, TypeClearStats, TypeClearFootprints, TypeClearTimes, TypeAiPrompt,
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };

    private readonly ILogger<ScheduleService> _logger;
    private readonly ChatService _chatService;
    private readonly PlayerStatsService _playerStats;
    private readonly PlayerFootprintService _footprints;
    private readonly PlayerTimeService _playerTimes;
    private readonly RoomMonitorService _roomMonitor;
    private readonly IGameManager _gameManager;
    private readonly AiService _ai;
    private readonly string _filePath;
    private readonly object _lock = new();
    private List<ScheduledTask> _tasks = new();
    private readonly object _runLock = new();
    private bool _running;
    private readonly HashSet<string> _aiInFlight = new();
    private DateTime _lastAiStartUtc = DateTime.MinValue;
    private Timer? _timer;
    private bool _dirty;

    public ScheduleService(
        ILogger<ScheduleService> logger,
        ChatService chatService,
        PlayerStatsService playerStats,
        PlayerFootprintService footprints,
        PlayerTimeService playerTimes,
        RoomMonitorService roomMonitor,
        IGameManager gameManager,
        AiService ai)
    {
        _logger = logger;
        _chatService = chatService;
        _playerStats = playerStats;
        _footprints = footprints;
        _playerTimes = playerTimes;
        _roomMonitor = roomMonitor;
        _gameManager = gameManager;
        _ai = ai;
        _filePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "webadmin_schedule.json");
        Load();
    }

    public void Start()
    {
        if (_timer != null)
        {
            return;
        }
        // 30s tick; the first tick is delayed 15s so the server finishes booting.
        _timer = new Timer(_ => Tick(), null, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(30));
        _logger.LogInformation("[Schedule] Started with {Count} task(s) ({Enabled} enabled).",
            _tasks.Count, _tasks.Count(t => t.Enabled));
    }

    public void Dispose()
    {
        _timer?.Dispose();
        _timer = null;
    }

    // ========== Persistence ==========

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_filePath))
            {
                return;
            }
            try
            {
                var json = File.ReadAllText(_filePath);
                var data = JsonSerializer.Deserialize<List<ScheduledTask>>(json, JsonOptions);
                if (data != null)
                {
                    // 重启恢复：进程中断会把 AI 任务留在"后台执行中"状态，
                    // 该状态已无人回写 —— 归位为失败，避免 ⏳ 永久挂起
                    // （每日任务若不归位会等到明天才重跑）。
                    foreach (var t in data)
                    {
                        if (t.LastRunOk == null)
                        {
                            t.LastRunOk = false;
                            t.LastRunResult = "Interrupted by server restart.";
                        }
                    }
                    _tasks = data;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[Schedule] Failed to load {File}; starting empty.", _filePath);
            }
        }
    }

    private void Save()
    {
        try
        {
            lock (_lock)
            {
                var json = JsonSerializer.Serialize(_tasks, JsonOptions);
                // 原子写：先写临时文件再替换，崩溃/断电不会留下被截断的
                // 半个文件（否则 Load 会静默清空全部任务）。
                var tmp = _filePath + ".tmp";
                File.WriteAllText(tmp, json);
                File.Move(tmp, _filePath, overwrite: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Schedule] Failed to save {File}.", _filePath);
        }
    }

    // ========== Public API (called from the controller) ==========

    public List<ScheduledTask> GetAll()
    {
        lock (_lock)
        {
            return _tasks.Select(Clone).ToList();
        }
    }

    public ScheduledTask? GetById(string id)
    {
        lock (_lock)
        {
            return _tasks.FirstOrDefault(t => t.Id == id) is { } found ? Clone(found) : null;
        }
    }

    /// <summary>Creates a task (validated). Returns (ok, error, task).</summary>
    public (bool Ok, string Error, ScheduledTask? Task) Create(ScheduledTask task, string createdBy)
    {
        var err = Validate(task);
        if (err != null)
        {
            return (false, err, null);
        }

        task.Id = Guid.NewGuid().ToString("N");
        task.CreatedBy = createdBy ?? string.Empty;
        task.CreatedAt = DateTime.UtcNow;
        task.LastRunAt = null;
        task.LastRunOk = null;
        task.LastRunResult = string.Empty;
        task.RunCount = 0;

        lock (_lock)
        {
            if (_tasks.Count >= 200)
            {
                return (false, "Too many scheduled tasks (max 200).", null);
            }
            _tasks.Add(task);
        }
        Save();
        _logger.LogInformation("[Schedule] Task created: {Name} ({Type}) by {User}.", task.Name, task.Type, task.CreatedBy);
        return (true, string.Empty, Clone(task));
    }

    public bool Delete(string id)
    {
        bool removed;
        lock (_lock)
        {
            removed = _tasks.RemoveAll(t => t.Id == id) > 0;
        }
        if (removed)
        {
            Save();
        }
        return removed;
    }

    public ScheduledTask? SetEnabled(string id, bool enabled)
    {
        ScheduledTask? result = null;
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task != null)
            {
                task.Enabled = enabled;
                result = Clone(task);
            }
        }
        if (result != null)
        {
            Save();
        }
        return result;
    }

    // ========== Scheduler ==========

    private void Tick()
    {
        if (_running)
        {
            return;
        }
        lock (_runLock)
        {
            if (_running)
            {
                return;
            }
            _running = true;
        }

        try
        {
            List<ScheduledTask> due;
            lock (_lock)
            {
                var nowUtc = DateTime.UtcNow;
                var nowLocal = DateTime.Now;
                due = _tasks.Where(t => t.Enabled && IsDue(t, nowUtc, nowLocal)).Select(Clone).ToList();
            }

            foreach (var task in due)
            {
                RunNow(task.Id, manual: false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Schedule] Tick failed.");
        }
        finally
        {
            lock (_runLock)
            {
                _running = false;
            }
        }
    }

    private static bool IsDue(ScheduledTask t, DateTime nowUtc, DateTime nowLocal)
    {
        // 单次任务：仅当到达指定时刻且从未执行过时触发一次；
        // 执行后 RunNow 会将其停用，双保险不再触发。
        if (t.RunOnceAt != null)
        {
            return t.Enabled && t.LastRunAt == null && nowUtc >= t.RunOnceAt.Value;
        }

        if (t.IntervalMinutes > 0)
        {
            if (t.LastRunAt == null)
            {
                return true;
            }
            return nowUtc - t.LastRunAt.Value >= TimeSpan.FromMinutes(Math.Max(1, t.IntervalMinutes));
        }

        if (string.IsNullOrWhiteSpace(t.DailyTime) || !TimeSpan.TryParseExact(t.DailyTime, "hh\\:mm", null, out var timeOfDay))
        {
            return false;
        }
        if (nowLocal.TimeOfDay < timeOfDay)
        {
            return false;
        }
        // Already ran today (local)?
        if (t.LastRunAt != null && t.LastRunAt.Value.ToLocalTime().Date == nowLocal.Date)
        {
            return false;
        }
        // Created after today's scheduled time? Then today's occurrence never
        // existed for this task — don't catch-up-fire 30s after creation.
        if (t.LastRunAt == null && t.CreatedAt.ToLocalTime() >= nowLocal.Date + timeOfDay)
        {
            return false;
        }
        return true;
    }

    /// <summary>Executes a task now (manual or by scheduler) and records the outcome.</summary>
    public (bool Ok, string Message) RunNow(string id, bool manual)
    {
        ScheduledTask snapshot;
        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task == null)
            {
                return (false, "Task not found.");
            }
            snapshot = Clone(task);
        }

        // AI 提示词任务：LLM 调用可能耗时 30 秒以上，绝不能阻塞调度心跳
        // 或面板请求 —— 标记"后台执行中"后放到线程池跑，完成后回写结果。
        if (snapshot.Type == TypeAiPrompt)
        {
            lock (_lock)
            {
                // 防重入：同一任务在执行中不允许再次触发（双击"立即执行"等）
                if (_aiInFlight.Contains(id))
                {
                    return (false, "AI 任务已在后台执行中，请等待完成。");
                }
                // 全局节流：计划任务调用 AI 不经过面板限流器，这里限最小 20 秒一次
                if (DateTime.UtcNow - _lastAiStartUtc < TimeSpan.FromSeconds(20))
                {
                    return (false, "AI 任务执行过于频繁，请稍后再试。");
                }
                _aiInFlight.Add(id);
                _lastAiStartUtc = DateTime.UtcNow;
                var task = _tasks.FirstOrDefault(t => t.Id == id);
                if (task != null)
                {
                    task.LastRunAt = DateTime.UtcNow;
                    task.LastRunOk = null;
                    task.LastRunResult = "AI 任务后台执行中…";
                    task.RunCount++;
                }
            }
            Save();
            _ = Task.Run(() => RunAiPromptAsync(snapshot));
            return (true, "AI 任务已在后台执行，结果将写入任务状态与 AI 聊天记录。");
        }

        // 自动触发时在执行前复查到期条件，缩小与手动执行交错导致的重复执行窗口
        if (!manual)
        {
            lock (_lock)
            {
                var task = _tasks.FirstOrDefault(t => t.Id == id);
                if (task == null || !IsDue(task, DateTime.UtcNow, DateTime.Now))
                {
                    return (false, "Skipped (already executed or no longer due).");
                }
            }
        }

        var (ok, message) = Execute(snapshot);
        if (snapshot.RunOnceAt != null)
        {
            message += ok ? "（单次任务已完成并自动停用）" : "（单次任务已自动停用）";
        }

        lock (_lock)
        {
            var task = _tasks.FirstOrDefault(t => t.Id == id);
            if (task != null)
            {
                task.LastRunAt = DateTime.UtcNow;
                task.LastRunOk = ok;
                task.LastRunResult = message.Length > 500 ? message[..500] : message;
                task.RunCount++;
                if (snapshot.RunOnceAt != null)
                {
                    task.Enabled = false;
                }
            }
        }
        Save();

        _logger.LogInformation("[Schedule] Task {Name} ({Mode}) {Result}.", snapshot.Name, manual ? "manual" : "auto", ok ? "succeeded: " + message : "failed: " + message);
        return (ok, message);
    }

    /// <summary>
    ///     Background executor for run_ai_prompt tasks: sends the stored prompt
    ///     to the panel AI (no shared conversation context) and records the
    ///     reply in the task state AND the AI chat history (kind=schedule),
    ///     so admins can read the full result in the panel.
    /// </summary>
    private async Task RunAiPromptAsync(ScheduledTask task)
    {
        try
        {
            var prompt = task.Message?.Trim() ?? string.Empty;
            _logger.LogInformation("[Schedule] AI task '{Name}' started (prompt {Len} chars).", task.Name, prompt.Length);
            // context="full"：附带服务器近期数据快照（行为日志/战绩/足迹/举报/聊天），
            // 否则"总结今日数据"类提示词无法产生有效结果。
            // 120 秒超时保险：AI 服务无响应时任务状态回到失败，绝不永久 ⏳。
            var callTask = _ai.PanelChatAsync(prompt, "full", "⏰ 定时任务 · " + task.Name, "schedule", useContext: false);
            var finished = await Task.WhenAny(callTask, Task.Delay(TimeSpan.FromSeconds(120)));
            bool ok; string result;
            if (finished != callTask)
            {
                ok = false;
                result = "AI 调用超时（120 秒无响应），请检查面板 AI 设置里的接入点与网络。";
                _logger.LogWarning("[Schedule] AI task '{Name}' timed out after 120s.", task.Name);
            }
            else
            {
                var (callOk, reply, error) = await callTask;
                ok = callOk;
                result = ok
                    ? (reply ?? "").Replace("\r", " ").Replace("\n", " ").Trim()
                    : (error ?? "AI 无回复");
            }
            lock (_lock)
            {
                var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
                if (t != null)
                {
                    t.LastRunOk = ok;
                    t.LastRunResult = result.Length > 500 ? result[..500] : result;
                    if (task.RunOnceAt != null)
                    {
                        t.Enabled = false; // 单次 AI 任务执行完成即停用
                    }
                }
            }
            Save();
            if (ok) { _logger.LogInformation("[Schedule] AI task '{Name}' succeeded, reply {Len} chars.", task.Name, result.Length); }
            else { _logger.LogWarning("[Schedule] AI task '{Name}' failed: {Result}", task.Name, result); }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Schedule] AI task {Name} failed.", task.Name);
            lock (_lock)
            {
                var t = _tasks.FirstOrDefault(x => x.Id == task.Id);
                if (t != null)
                {
                    t.LastRunOk = false;
                    t.LastRunResult = "Error: " + ex.GetType().Name;
                }
            }
            Save();
        }
        finally
        {
            lock (_lock)
            {
                _aiInFlight.Remove(task.Id);
            }
        }
    }

    private (bool Ok, string Message) Execute(ScheduledTask task)
    {
        try
        {
            switch (task.Type)
            {
                case TypeSendChat:
                {
                    var message = task.Message?.Trim() ?? string.Empty;
                    if (message.Length == 0)
                    {
                        return (false, "Empty message.");
                    }
                    var codes = task.GameCodes ?? new List<string>();
                    if (codes.Count == 0)
                    {
                        codes = _gameManager.Games.Select(g => g.Code.Code).ToList();
                    }
                    if (codes.Count == 0)
                    {
                        return (false, "No active games to send to.");
                    }
                    var delivered = _chatService.SendPublicMessageToGamesAsync(codes, message, "定时任务").GetAwaiter().GetResult();
                    return delivered > 0
                        ? (true, "Sent to " + delivered + " room(s).")
                        : (false, "No matching active rooms (codes may be stale or rooms closed).");
                }
                case TypeGroupMessage:
                {
                    var message = task.Message?.Trim() ?? string.Empty;
                    if (message.Length == 0)
                    {
                        return (false, "Empty message.");
                    }
                    var delivered = _roomMonitor.SendAnnouncementToGroupsAsync(message).GetAwaiter().GetResult();
                    return delivered > 0
                        ? (true, "Delivered to " + delivered + " QQ group(s).")
                        : (false, "QQ broadcast disabled, no groups, or all sends failed.");
                }
                case TypeClearStats:
                    _playerStats.ClearAllAsync().GetAwaiter().GetResult();
                    return (true, "Player stats cleared.");
                case TypeClearFootprints:
                    _footprints.ClearAllAsync().GetAwaiter().GetResult();
                    return (true, "Footprints cleared.");
                case TypeClearTimes:
                    _playerTimes.ClearAll();
                    return (true, "Player times cleared.");
                default:
                    return (false, "Unknown task type: " + task.Type);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Schedule] Task {Name} execution failed.", task.Name);
            return (false, "Error: " + ex.GetType().Name);
        }
    }

    // ========== Validation ==========

    public static string? Validate(ScheduledTask task)
    {
        if (task == null)
        {
            return "Task body required.";
        }
        if (string.IsNullOrWhiteSpace(task.Name) || task.Name.Length > 60)
        {
            return "Task name required (max 60 chars).";
        }
        if (!KnownTypes.Contains(task.Type))
        {
            return "Unknown task type.";
        }
        if (task.IntervalMinutes > 0)
        {
            if (task.RunOnceAt != null)
            {
                return "A one-shot task cannot also have an interval.";
            }
            if (task.IntervalMinutes < 5 || task.IntervalMinutes > 10080)
            {
                return "Interval must be between 5 and 10080 minutes.";
            }
        }
        else if (task.RunOnceAt != null)
        {
            // 单次任务：时间合法性在下方 RunOnceAt 分支校验，
            // 不要求 dailyTime
        }
        else
        {
            if (string.IsNullOrWhiteSpace(task.DailyTime) || !TimeSpan.TryParseExact(task.DailyTime, "hh\\:mm", null, out var tod) || tod < TimeSpan.Zero || tod >= TimeSpan.FromDays(1))
            {
                return "DailyTime must be HH:mm.";
            }
        }
        if (task.Type == TypeSendChat || task.Type == TypeGroupMessage)
        {
            if (string.IsNullOrWhiteSpace(task.Message))
            {
                return "Message required for this task type.";
            }
            if (task.Message.Length > 800)
            {
                return "Message too long (max 800 chars).";
            }
        }
        if (task.Type == TypeAiPrompt)
        {
            if (string.IsNullOrWhiteSpace(task.Message))
            {
                return "AI prompt required for this task type.";
            }
            if (task.Message.Length > 4000)
            {
                return "AI prompt too long (max 4000 chars).";
            }
        }
        if (task.GameCodes != null && task.GameCodes.Count > 64)
        {
            return "Too many game codes.";
        }
        if (task.RunOnceAt != null)
        {
            if (task.IntervalMinutes > 0 || !string.IsNullOrWhiteSpace(task.DailyTime))
            {
                return "A one-shot task cannot also have an interval or daily time.";
            }
            if (task.RunOnceAt.Value <= DateTime.UtcNow.AddSeconds(30))
            {
                return "One-shot time must be in the future (at least 30 seconds ahead).";
            }
        }
        return null;
    }

    private static ScheduledTask Clone(ScheduledTask t)
    {
        return new ScheduledTask
        {
            Id = t.Id,
            Name = t.Name,
            Type = t.Type,
            GameCodes = t.GameCodes != null ? new List<string>(t.GameCodes) : new List<string>(),
            Message = t.Message ?? string.Empty,
            IntervalMinutes = t.IntervalMinutes,
            DailyTime = t.DailyTime ?? string.Empty,
            RunOnceAt = t.RunOnceAt,
            Enabled = t.Enabled,
            CreatedBy = t.CreatedBy ?? string.Empty,
            CreatedAt = t.CreatedAt,
            LastRunAt = t.LastRunAt,
            LastRunOk = t.LastRunOk,
            LastRunResult = t.LastRunResult ?? string.Empty,
            RunCount = t.RunCount,
        };
    }
}