using System;

namespace WhaleCompanion
{
    internal static class Skins
    {
        public static readonly string[] Poses = { "idle-cute", "greet", "wink", "blush", "daily-coffee", "work-laptop", "thinking", "sleep", "eat", "celebrate", "pick-up", "failure" };
        public static readonly string[] Names = { "开心待机", "挥手", "眨眼", "害羞", "喝咖啡", "认真工作", "思考", "睡觉", "干饭", "庆祝", "被拎起来", "委屈" };
        public static string Normalize(string skin) { return skin == "carrot" ? "carrot" : "whale"; }
        public static int Frame(string pose)
        {
            int exact = Array.IndexOf(Poses, pose);
            if (exact >= 0) return exact;
            if (pose.Contains("sleep") || pose.Contains("tired")) return 7;
            if (pose.Contains("cry") || pose.Contains("angry") || pose.Contains("lose") || pose.Contains("sad")) return 11;
            if (pose.Contains("work") || pose == "tool" || pose.Contains("coding") || pose.Contains("gaming")) return 5;
            if (pose.Contains("think") || pose.Contains("wait")) return 6;
            if (pose.Contains("head") || pose.Contains("heart") || pose.Contains("love")) return 3;
            if (pose.Contains("tail") || pose.Contains("smug")) return 2;
            if (pose.Contains("eat") || pose.Contains("food")) return 8;
            if (pose.Contains("star") || pose.Contains("success") || pose.Contains("win") || pose.Contains("level")) return 9;
            if (pose.Contains("stretch") || pose.Contains("hello")) return 1;
            return 0;
        }
    }
}
