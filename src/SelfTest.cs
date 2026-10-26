using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace WhaleCompanion
{
    internal static class SelfTest
    {
        private static int checks;
        private static void Check(bool condition, string name)
        { if (!condition) throw new InvalidOperationException("FAIL: " + name); checks++; }
        private static string Event(string payload)
        { return "{\"timestamp\":\"2026-10-26T01:00:00Z\",\"type\":\"event_msg\",\"payload\":" + payload + "}"; }
        private static string Start(string id)
        { return Event("{\"type\":\"task_started\",\"turn_id\":\"" + id + "\"}"); }
        private static string Usage(long input, long cached, long output)
        { return Event("{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":" + cached + ",\"output_tokens\":" + output + "}}}"); }
        private static string ThreadUsage(long input, long cached, long output)
        { return "{\"type\":\"token_usage_record\",\"payload\":{\"thread_token_usage\":{\"input_tokens\":" + input + ",\"cached_input_tokens\":" + cached + ",\"output_tokens\":" + output + "}}}"; }
        private static string Item(string turn, string id, string kind, long start, long end)
        { return Event("{\"type\":\"item_completed\",\"turn_id\":\"" + turn + "\",\"item\":{\"id\":\"" + id + "\",\"type\":\"" + kind + "\"},\"started_at_ms\":" + start + ",\"completed_at_ms\":" + end + "}"); }
        public static int Run()
        {
            var lines = new List<string>();
            try
            {
                var m = new SessionMetrics(); m.Apply(Start("one")); m.Apply(Usage(1000, 800, 100));
                var oldPrefs = Json.Serializer().Deserialize<Preferences>("{\"X\":123,\"Y\":456,\"Scale\":0.85}");
                Check(oldPrefs.Skin == "whale" && oldPrefs.X == 123 && oldPrefs.Scale == 0.85, "old preferences retain placement and default whale skin");
                oldPrefs.Skin = "carrot";
                var restored = Json.Serializer().Deserialize<Preferences>(Json.Serializer().Serialize(oldPrefs));
                Check(restored.Skin == "carrot" && restored.X == 123 && restored.Y == 456, "selected skin persists with placement");
                Check(Skins.Normalize(null) == "whale" && Skins.Normalize("unknown") == "whale", "unknown skin uses supported default");
                for (int i = 0; i < Skins.Poses.Length; i++) Check(Skins.Frame(Skins.Poses[i]) == i, "carrot distinct pose " + i);
                Check(Skins.Frame("tool") == 5 && Skins.Frame("react-head") == 3 && Skins.Frame("star") == 9, "production work and touch actions map to carrot emotions");
                m.Apply(Item("one", "a", "Reasoning", 1000, 3000)); m.Apply(Item("one", "b", "AgentMessage", 2000, 5000));
                Check(Math.Abs(m.Speed.Value - 25) < 0.0001, "overlapping model intervals count once");
                Check(m.Total == 1100 && m.CachePercent == 80, "cache is subset of input");
                m.Apply(Usage(1000, 800, 100)); m.Apply(Item("one", "a", "Reasoning", 1000, 3000));
                Check(m.Output == 100 && m.Speed == 25, "duplicate usage and items do not inflate TPS");
                m.Apply(Item("one", "tool", "CommandExecution", 5000, 90000));
                Check(m.Speed == 25, "tool time excluded");
                m.Apply(Event("{\"type\":\"task_complete\",\"turn_id\":\"one\",\"time_to_first_token_ms\":1500}"));
                Check(!m.Active && m.Speed == 25 && m.FirstTokenSeconds == 1.5, "completed turn retains sampled rate and TTFT");
                m.Apply(Start("two")); Check(!m.Speed.HasValue && m.Active, "new turn clears old rate");
                m.Apply(Item("one", "late", "AgentMessage", 100000, 110000));
                Check(!m.Speed.HasValue, "foreign turn windows ignored");
                m.Apply(Usage(2000, 1600, 130)); m.Apply(Item("two", "c", "AgentMessage", 100000, 103000));
                Check(m.Speed == 10, "new turn uses cumulative output difference");
                m.Apply("{\"type\":\"token_usage_record\",\"payload\":{\"thread_token_usage\":{\"input_tokens\":2000,\"cached_input_tokens\":1600,\"output_tokens\":130}}}");
                Check(m.Total == 2130 && m.Speed == 10, "two usage carriers replace not add");
                m.Apply(Start("short")); m.Apply(Usage(2200, 1700, 132)); m.Apply(Item("short", "short", "AgentMessage", 200000, 200020));
                Check(!m.Speed.HasValue, "tiny noisy window suppressed");
                m.Apply("broken{"); Check(m.MalformedLines == 1, "malformed line recorded");
                m.Apply(Usage(1, 2, 4)); Check(m.Input == 2200 && m.MalformedLines == 2, "invalid cache counter rejected");
                var truncated = new SessionMetrics { TruncatedHistory = true }; truncated.Apply(Start("tail"));
                truncated.Apply(Usage(10000, 9000, 500)); truncated.Apply(Item("tail", "t", "AgentMessage", 1000, 3000));
                Check(!truncated.Speed.HasValue, "missing turn baseline never invents speed");
                var zero = new SessionMetrics(); Check(!zero.CachePercent.HasValue && !zero.Speed.HasValue, "unknown values remain unknown");
                zero.Apply(Event("{\"type\":\"token_count\",\"info\":{\"total_token_usage\":{\"input_tokens\":100,\"output_tokens\":10}}}"));
                Check(zero.Total == 110 && !zero.CachePercent.HasValue, "missing cache category is unknown rather than zero");

                var mixed = new SessionMetrics(); mixed.Apply(Usage(1000, 800, 100)); mixed.Apply(ThreadUsage(5000, 4000, 4100));
                mixed.Apply(Start("offset")); mixed.Apply(Item("offset", "reason", "Reasoning", 1000, 3000));
                mixed.Apply(ThreadUsage(5100, 4080, 4200));
                Check(!mixed.Speed.HasValue, "secondary carrier cannot inflate preferred counter before its update");
                mixed.Apply(Usage(1100, 880, 200));
                Check(mixed.Speed == 50 && mixed.Output == 200 && mixed.Total == 1300, "different carrier origins keep independent baselines");
                mixed.Apply(ThreadUsage(5100, 4080, 4200)); mixed.Apply(Usage(1100, 880, 200));
                Check(mixed.Speed == 50 && mixed.HasBaseline, "alternating offset carriers do not trigger rollback protection");
                mixed.Apply(Item("offset", "reply", "AgentMessage", 4000, 6000)); mixed.Apply(ThreadUsage(5200, 4160, 4300)); mixed.Apply(Usage(1200, 960, 300));
                Check(mixed.Speed == 50, "later model segments keep paired token deltas and timing");
                mixed.Apply(Start("next-offset"));
                Check(!mixed.Speed.HasValue, "mixed-carrier new turn clears previous speed");
                mixed.Apply(ThreadUsage(5300, 4240, 4340)); mixed.Apply(Usage(1300, 1040, 340)); mixed.Apply(Item("next-offset", "next", "Reasoning", 8000, 10000));
                Check(mixed.Speed == 20, "mixed carriers retain independent baselines across turns");
                var fallback = new SessionMetrics(); fallback.Apply(ThreadUsage(1000, 800, 100)); fallback.Apply(Start("fallback"));
                fallback.Apply(ThreadUsage(1200, 900, 140)); fallback.Apply(Item("fallback", "f", "AgentMessage", 1000, 3000));
                Check(fallback.Speed == 20 && fallback.Total == 1340, "thread-only logs still provide a valid sample");
                fallback.Apply(Usage(5000, 4000, 900));
                Check(fallback.Speed == 20 && fallback.Output == 140, "new carrier with unknown origin cannot replace a valid baseline");
                fallback.Apply(Start("both-known")); fallback.Apply(Usage(5200, 4100, 920)); fallback.Apply(Item("both-known", "known", "AgentMessage", 5000, 7000));
                Check(fallback.Speed == 10 && fallback.Output == 920, "preferred carrier is restored when its next baseline is known");
                var reset = new SessionMetrics(); reset.Apply(Usage(1000, 800, 100)); reset.Apply(Start("reset"));
                reset.Apply(Usage(1100, 880, 120)); reset.Apply(Item("reset", "r", "AgentMessage", 1000, 3000)); reset.Apply(Usage(100, 80, 10));
                Check(!reset.Speed.HasValue, "real rollback in the same carrier still invalidates its baseline");

                string temp = Path.Combine(Program.Root, "selftest-tail.tmp");
                try
                {
                    string first = Start("utf8") + "\n";
                    File.WriteAllText(temp, first, new UTF8Encoding(false)); var tail = new SessionTail(temp); tail.Read();
                    string context = "{\"type\":\"turn_context\",\"payload\":{\"model\":\"测试模型\"}}\n";
                    byte[] bytes = Encoding.UTF8.GetBytes(context); int cut = Array.IndexOf(bytes, (byte)0xE6) + 1;
                    using (var stream = new FileStream(temp, FileMode.Append)) stream.Write(bytes, 0, cut);
                    tail.Read(); Check(tail.Metrics.Model == "未知模型", "partial UTF8 record not parsed");
                    using (var stream = new FileStream(temp, FileMode.Append)) stream.Write(bytes, cut, bytes.Length - cut);
                    tail.Read(); Check(tail.Metrics.Model == "测试模型" && tail.Metrics.MalformedLines == 0, "split UTF8 bytes reconstructed");
                    File.WriteAllText(temp, Usage(10, 8, 2) + "\n", new UTF8Encoding(false)); tail.Read();
                    Check(tail.Metrics.Total == 12, "truncated file recovery");
                }
                finally { if (File.Exists(temp)) File.Delete(temp); }

                string fixtureRoot = Path.Combine(Program.Root, "selftest-monitor-" + Guid.NewGuid().ToString("N"));
                string year = Path.Combine(fixtureRoot, "sessions", DateTime.Now.ToString("yyyy"));
                string month = Path.Combine(year, DateTime.Now.ToString("MM"));
                string day = Path.Combine(month, DateTime.Now.ToString("dd"));
                Directory.CreateDirectory(day);
                string userLog = Path.Combine(day, "user.jsonl"), reviewLog = Path.Combine(day, "review.jsonl");
                try
                {
                    File.WriteAllText(userLog, "{\"type\":\"turn_context\",\"payload\":{\"model\":\"test-user-model\"}}\n" + Usage(1000, 800, 100) + "\n" + Start("normal") + "\n" + Usage(1200, 900, 140) + "\n" + Item("normal", "answer", "AgentMessage", 1000, 3000) + "\n");
                    File.WriteAllText(reviewLog, "{\"timestamp\":\"2099-01-01T00:00:00Z\",\"type\":\"turn_context\",\"payload\":{\"model\":\"codex-auto-review\"}}\n");
                    var automatic = new SessionMonitor(fixtureRoot); automatic.Poll();
                    Check(automatic.SelectedPath == userLog && automatic.Current.Model == "test-user-model" && automatic.Current.Speed == 20,
                        "newer internal approval review cannot steal automatic tracking or speed");
                    var pinned = new SessionMonitor(fixtureRoot) { FollowLatest = false, PinnedPath = reviewLog }; pinned.Poll();
                    Check(pinned.SelectedPath == reviewLog, "explicitly pinned session selection remains respected");
                    File.Delete(userLog); var reviewsOnly = new SessionMonitor(fixtureRoot); reviewsOnly.Poll();
                    Check(reviewsOnly.Current == null && reviewsOnly.SelectedPath == "", "internal reviews alone do not masquerade as a conversation");
                    File.WriteAllText(userLog, "{\"type\":\"turn_context\",\"payload\":{\"model\":\"long-turn-model\"}}\n" + Usage(1000, 800, 100) + "\n" + Start("long") + "\n{\"type\":\"response_item\",\"payload\":{\"padding\":\"" + new string('x', 4 * 1024 * 1024 + 100) + "\"}}\n" + Usage(1200, 900, 140) + "\n" + Item("long", "long-answer", "AgentMessage", 1000, 3000) + "\n");
                    var longTail = new SessionTail(userLog); longTail.Read();
                    Check(longTail.Metrics.Model == "long-turn-model" && longTail.Metrics.Speed == 20 && longTail.Metrics.HasBaseline,
                        "restart during a turn larger than tail window recovers model and paired baseline");
                }
                finally
                {
                    if (File.Exists(userLog)) File.Delete(userLog);
                    if (File.Exists(reviewLog)) File.Delete(reviewLog);
                    Directory.Delete(day); Directory.Delete(month); Directory.Delete(year);
                    Directory.Delete(Path.Combine(fixtureRoot, "sessions")); Directory.Delete(fixtureRoot);
                }

                var spring = new Spring { Value = 0.25, Velocity = 2 };
                for (int i = 0; i < 600; i++) spring.Step(0, 1.0 / 60);
                Check(Math.Abs(spring.Value) < 0.000001 && Math.Abs(spring.Velocity) < 0.000001, "spring settles");
                var glide = new Glide { X = 500, Y = 500 }; glide.Release(5000, 0);
                for (int i = 0; i < 300; i++) glide.Step(1.0 / 60, 0, 0, 1000, 1000);
                Check(!glide.Moving && glide.X > 500 && glide.X < 523, "release glides only a short bounded distance");
                glide.X = 999; glide.Vx = 200; Check(glide.Step(0.03, 0, 0, 1000, 1000) && glide.X == 1000 && glide.Vx < 0, "edge collision rebounds within bounds");
                var sixty = new Glide { X = 300, Vx = 100 }; var thirty = new Glide { X = 300, Vx = 100 };
                for (int i = 0; i < 60; i++) sixty.Step(1.0 / 60, 0, 0, 1000, 1000);
                for (int i = 0; i < 30; i++) thirty.Step(1.0 / 30, 0, 0, 1000, 1000);
                Check(Math.Abs(sixty.X - thirty.X) < 0.1, "glide independent of frame rate");
                lines.Add("PASS " + checks + " checks");
                File.WriteAllLines(Path.Combine(Program.Root, "selftest-result.txt"), lines);
                Console.WriteLine(lines[0]); return 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Program.Root, "selftest-result.txt"), ex.ToString()); Console.WriteLine(ex); return 1; }
        }
        public static int Probe(string[] args)
        {
            try
            {
                string home = Environment.GetEnvironmentVariable("CODEX_HOME");
                if (String.IsNullOrWhiteSpace(home)) home = Program.DefaultCodexHome();
                var monitor = new SessionMonitor(home); monitor.Poll(); var m = monitor.Current;
                string result = Json.Serializer().Serialize(new { source = monitor.SelectedPath, error = monitor.Error,
                    model = m == null ? null : m.Model, state = m == null ? null : m.State,
                    input = m == null ? 0 : m.Input, cached = m == null ? 0 : m.Cached, output = m == null ? 0 : m.Output,
                    speed = m == null ? null : m.Speed, hasBaseline = m != null && m.HasBaseline,
                    malformed = m == null ? 0 : m.MalformedLines });
                File.WriteAllText(Path.Combine(Program.Root, "probe-result.json"), result); Console.WriteLine(result);
                return monitor.Error.Length == 0 && m != null ? 0 : 1;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(Program.Root, "probe-result.json"), ex.ToString()); return 1; }
        }
    }
}
