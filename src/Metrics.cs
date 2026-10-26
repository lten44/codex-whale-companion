using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace WhaleCompanion
{
    internal static class Json
    {
        public static Dictionary<string, object> Map(object value) { return value as Dictionary<string, object>; }
        public static object Get(Dictionary<string, object> d, string key) { object v; return d != null && d.TryGetValue(key, out v) ? v : null; }
        public static Dictionary<string, object> Child(Dictionary<string, object> d, string key) { return Map(Get(d, key)); }
        public static string Str(Dictionary<string, object> d, string key) { return Convert.ToString(Get(d, key), CultureInfo.InvariantCulture); }
        public static long Num(Dictionary<string, object> d, string key) { long n; return long.TryParse(Str(d, key), out n) ? n : 0; }
        public static JavaScriptSerializer Serializer() { return new JavaScriptSerializer { MaxJsonLength = 64 * 1024 * 1024, RecursionLimit = 120 }; }
    }

    internal sealed class TimeWindow
    {
        public long Start, End;
        public TimeWindow(long start, long end) { Start = start; End = end; }
    }

    internal sealed class SessionMetrics
    {
        private sealed class UsageCounter
        {
            public long Input, Cached, Output, Baseline;
            public bool HasUsage, HasCache, HasBaseline;
            public DateTime LastUsageUtc = DateTime.MinValue;
            public void BeginTurn(bool completeHistory)
            { Baseline = Output; HasBaseline = HasUsage || completeHistory; }
        }
        // These carriers can have different cumulative origins after compaction.
        // Keep their baselines separate; prefer token_count when it is available.
        private readonly UsageCounter eventUsage = new UsageCounter();
        private readonly UsageCounter threadUsage = new UsageCounter();
        public string Id = "", Model = "未知模型", TurnId = "", State = "未开始", Tool = "";
        public long Input, Cached, Output;
        public long TurnOutputBaseline;
        public bool HasUsage, HasCache, HasBaseline, Active, Failed;
        public double? Speed, FirstTokenSeconds;
        public DateTime LastUsageUtc = DateTime.MinValue, LastEventUtc = DateTime.MinValue;
        public int MalformedLines, OversizedLines;
        public SessionMetrics Snapshot() { return (SessionMetrics)MemberwiseClone(); }
        private readonly List<TimeWindow> windows = new List<TimeWindow>();
        private readonly HashSet<string> itemIds = new HashSet<string>();
        public long Total { get { return Input + Output; } }
        public double? CachePercent { get { return HasCache && Input > 0 ? (double?) (100.0 * Cached / Input) : null; } }

        public static long UnionMs(IEnumerable<TimeWindow> source)
        {
            long total = 0, start = -1, end = -1;
            foreach (TimeWindow w in source.Where(x => x.Start > 0 && x.End > x.Start).OrderBy(x => x.Start))
            {
                if (start < 0) { start = w.Start; end = w.End; }
                else if (w.Start <= end) end = Math.Max(end, w.End);
                else { total += end - start; start = w.Start; end = w.End; }
            }
            return total + (start < 0 ? 0 : end - start);
        }

        private void Recompute()
        {
            UsageCounter selected = eventUsage.HasUsage && (eventUsage.HasBaseline || !threadUsage.HasUsage || !threadUsage.HasBaseline) ? eventUsage : threadUsage;
            Input = selected.Input; Cached = selected.Cached; Output = selected.Output;
            HasUsage = selected.HasUsage; HasCache = selected.HasCache; HasBaseline = selected.HasBaseline;
            TurnOutputBaseline = selected.Baseline; LastUsageUtc = selected.LastUsageUtc;
            long duration = UnionMs(windows), tokens = selected.Output - selected.Baseline;
            Speed = selected.HasUsage && selected.HasBaseline && duration >= 500 && tokens > 0 ? (double?)(tokens * 1000.0 / duration) : null;
            if (Speed > 100000) Speed = null;
        }

        public void Apply(string line)
        {
            if (String.IsNullOrWhiteSpace(line)) return;
            Dictionary<string, object> root;
            try { root = Json.Serializer().DeserializeObject(line) as Dictionary<string, object>; }
            catch (ArgumentException) { MalformedLines++; return; }
            catch (InvalidOperationException) { MalformedLines++; return; }
            if (root == null) { MalformedLines++; return; }
            var p = Json.Child(root, "payload");
            string type = Json.Str(root, "type");
            DateTime stamp;
            bool dated = DateTime.TryParse(Json.Str(root, "timestamp"), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out stamp);
            if (dated && stamp > LastEventUtc) LastEventUtc = stamp;
            if (type == "session_meta") { Id = Json.Str(p, "id"); return; }
            if (type == "turn_context")
            {
                string model = Json.Str(p, "model");
                if (model.Length > 0) Model = model;
                return;
            }
            if (type == "token_usage_record")
            {
                var totals = Json.Child(p, "thread_token_usage");
                if (totals != null) SetUsage(threadUsage, totals, dated ? stamp : DateTime.MinValue);
                return;
            }
            if (type != "event_msg") return;
            string eventType = Json.Str(p, "type");
            if (eventType == "task_started" || eventType == "turn_started")
            {
                string id = Json.Str(p, "turn_id");
                if (id.Length > 0 && id == TurnId) return;
                TurnId = id; Active = true; Failed = false; State = "等待模型"; Tool = "";
                bool zeroOrigin = !TruncatedHistory && !HasUsage;
                eventUsage.BeginTurn(zeroOrigin); threadUsage.BeginTurn(zeroOrigin);
                windows.Clear(); itemIds.Clear(); Speed = null; FirstTokenSeconds = null;
                Recompute();
                return;
            }
            if (eventType == "token_count")
            {
                var info = Json.Child(p, "info");
                var totals = Json.Child(info, "total_token_usage");
                if (totals != null) SetUsage(eventUsage, totals, dated ? stamp : DateTime.MinValue);
                return;
            }
            if (eventType == "item_completed" || eventType == "item_started")
            {
                string turn = Json.Str(p, "turn_id");
                if (turn.Length > 0 && turn != TurnId) return;
                var item = Json.Child(p, "item");
                string kind = Json.Str(item, "type");
                if (eventType == "item_completed" && (kind == "Reasoning" || kind == "AgentMessage"))
                {
                    string itemId = Json.Str(item, "id");
                    long start = Json.Num(p, "started_at_ms"), end = Json.Num(p, "completed_at_ms");
                    if (itemIds.Add(itemId + ":" + start + ":" + end)) windows.Add(new TimeWindow(start, end));
                    if (Active) State = "生成中";
                    Recompute();
                }
                else if (kind == "CommandExecution" || kind == "McpToolCall" || kind == "FileChange" || kind == "WebSearch")
                {
                    Tool = kind;
                    if (Active) State = eventType == "item_started" ? "工具运行" : "思考中";
                }
                return;
            }
            if (eventType == "task_complete" || eventType == "turn_complete" || eventType == "turn_aborted")
            {
                string turn = Json.Str(p, "turn_id");
                if (turn.Length > 0 && TurnId.Length > 0 && turn != TurnId) return;
                Active = false; Failed = Json.Get(p, "error") != null;
                State = Failed ? "任务异常" : eventType == "turn_aborted" ? "已停止" : "已完成";
                if (Json.Get(p, "time_to_first_token_ms") != null) FirstTokenSeconds = Json.Num(p, "time_to_first_token_ms") / 1000.0;
                Recompute();
            }
        }

        public bool TruncatedHistory;
        private void SetUsage(UsageCounter counter, Dictionary<string, object> totals, DateTime stamp)
        {
            long input, cached = 0, output;
            bool hasCache = Json.Get(totals, "cached_input_tokens") != null;
            if (!Int64.TryParse(Json.Str(totals, "input_tokens"), out input) || !Int64.TryParse(Json.Str(totals, "output_tokens"), out output) ||
                (hasCache && !Int64.TryParse(Json.Str(totals, "cached_input_tokens"), out cached)) || input < 0 || cached < 0 || output < 0 || cached > input)
            { MalformedLines++; return; }
            bool changed = !counter.HasUsage || input != counter.Input || cached != counter.Cached || output != counter.Output;
            if (counter.HasUsage && output < counter.Output) counter.HasBaseline = false;
            counter.Input = input; counter.Cached = cached; counter.Output = output;
            counter.HasUsage = true; counter.HasCache = hasCache;
            if (changed) counter.LastUsageUtc = stamp;
            Recompute();
        }
    }

    internal sealed class SessionTail
    {
        public readonly string Path;
        public SessionMetrics Metrics = new SessionMetrics();
        private long offset;
        private readonly List<byte> pending = new List<byte>();
        private bool discardOversize;
        private byte[] prefix;
        private const int TailBytes = 4 * 1024 * 1024;
        public SessionTail(string path) { Path = path; }
        public void Read()
        {
            using (var f = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                bool replaced = f.Length < offset;
                if (prefix != null && offset > 0)
                {
                    byte[] current = new byte[prefix.Length]; int count = f.Read(current, 0, current.Length);
                    replaced = replaced || count != prefix.Length || !current.SequenceEqual(prefix);
                }
                if (replaced) { offset = 0; prefix = null; Metrics = new SessionMetrics(); pending.Clear(); discardOversize = false; }
                if (offset == 0 && f.Length > TailBytes)
                {
                    // Bounded recovery. Expand only when a tail contains no usage counters.
                    long window = TailBytes;
                    do
                    {
                        Metrics = new SessionMetrics { TruncatedHistory = f.Length > window };
                        pending.Clear(); discardOversize = false;
                        offset = Math.Max(0, f.Length - window); f.Position = offset;
                        if (offset > 0) { int b; while ((b = f.ReadByte()) != -1) { offset++; if (b == 10) break; } }
                        ReadNew(f);
                        if (Metrics.HasUsage || window >= f.Length) break;
                        window = Math.Min(f.Length, window * 2);
                    } while (true);
                }
                else { f.Position = offset; ReadNew(f); }
                if (prefix == null && f.Length > 0)
                {
                    f.Position = 0; prefix = new byte[(int)Math.Min(128, f.Length)];
                    f.Read(prefix, 0, prefix.Length);
                }
            }
        }
        private void ReadNew(FileStream f)
        {
            byte[] buffer = new byte[65536]; int n;
            while ((n = f.Read(buffer, 0, buffer.Length)) > 0)
            {
                offset += n;
                for (int i = 0; i < n; i++)
                {
                    byte b = buffer[i];
                    if (b == 10)
                    {
                        if (!discardOversize) Metrics.Apply(Encoding.UTF8.GetString(pending.ToArray()).TrimEnd('\r'));
                        pending.Clear(); discardOversize = false;
                    }
                    else if (!discardOversize)
                    {
                        if (pending.Count >= 32 * 1024 * 1024) { Metrics.OversizedLines++; pending.Clear(); discardOversize = true; }
                        else pending.Add(b);
                    }
                }
            }
        }
    }

    internal sealed class SessionMonitor
    {
        private readonly string home;
        private readonly Dictionary<string, SessionTail> tails = new Dictionary<string, SessionTail>(StringComparer.OrdinalIgnoreCase);
        public string SelectedPath = "", PinnedPath = "", Error = "";
        public bool FollowLatest = true;
        private DateTime lastDiscovery = DateTime.MinValue;
        private volatile SessionMetrics published;
        public SessionMonitor(string root) { home = root; }
        public SessionMetrics Current { get { return published; } }
        public List<string> Paths { get { return tails.Keys.ToList(); } }
        public void Poll()
        {
            Error = "";
            try
            {
                if ((DateTime.UtcNow - lastDiscovery).TotalSeconds >= 3)
                {
                    var files = new List<FileInfo>();
                    // Native session paths partition by local day; include yesterday at midnight.
                    for (int day = 0; day < 2; day++)
                    {
                        DateTime date = DateTime.Now.AddDays(-day);
                        string dir = System.IO.Path.Combine(home, "sessions", date.ToString("yyyy"), date.ToString("MM"), date.ToString("dd"));
                        if (Directory.Exists(dir)) files.AddRange(new DirectoryInfo(dir).GetFiles("*.jsonl"));
                    }
                    foreach (var info in files.OrderByDescending(x => x.LastWriteTimeUtc).Take(16))
                        if (!tails.ContainsKey(info.FullName)) tails.Add(info.FullName, new SessionTail(info.FullName));
                    if (PinnedPath.Length > 0 && File.Exists(PinnedPath) && !tails.ContainsKey(PinnedPath)) tails.Add(PinnedPath, new SessionTail(PinnedPath));
                    // Keep a bounded working set, plus the selected/pinned conversation.
                    var keep = new HashSet<string>(files.OrderByDescending(x => x.LastWriteTimeUtc).Take(16).Select(x => x.FullName), StringComparer.OrdinalIgnoreCase);
                    foreach (string path in tails.Keys.ToArray()) if (!keep.Contains(path) && path != SelectedPath && path != PinnedPath) tails.Remove(path);
                    lastDiscovery = DateTime.UtcNow;
                }
                foreach (SessionTail tail in tails.Values) tail.Read();
                if (!FollowLatest && tails.ContainsKey(PinnedPath)) SelectedPath = PinnedPath;
                else if (tails.Count > 0) SelectedPath = tails.OrderByDescending(x => x.Value.Metrics.LastEventUtc).First().Key;
                SessionTail selected;
                published = tails.TryGetValue(SelectedPath, out selected) ? selected.Metrics.Snapshot() : null;
            }
            catch (IOException ex) { Error = "日志暂时不可读：" + ex.GetType().Name; }
            catch (UnauthorizedAccessException) { Error = "没有读取 Codex 日志的权限"; }
        }
    }
}
