using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Conveer
{
    /// <summary>
    /// Настройки конвейера (conveer.json). Относительные пути считаются от папки, где лежит conveer.json,
    /// поэтому "gameRoot": "../game-token" – это папка игры рядом с папкой конвейера.
    /// </summary>
    public sealed class Config
    {
        public string GameRoot = "../game-token";
        public string Output = "out";
        public string Ffmpeg = "ffmpeg";
        public string Ffprobe = "ffprobe";
        public int Width = 1280;
        public int Height = 720;
        public int Fps = 30;
        public int Crf = 20;
        public string Preset = "veryfast";
        public string Subtitles = "burn";     // burn – вшить в кадр, soft – отдельной дорожкой, off – без субтитров
        public string Language = "ru";
        public float MasterVolume = 0.9f, MusicVolume = 0.5f, SfxVolume = 0.8f;
        public readonly Dictionary<string, float> Seconds = new Dictionary<string, float>
        {
            ["title"] = 30f, ["hub"] = 24f, ["biome"] = 30f, ["beforeBoss"] = 6f, ["bossFight"] = 50f,
            ["afterBoss"] = 14f, ["results"] = 10f, ["afterResults"] = 6f, ["transition"] = 10f,
        };
        public List<string> Scenes = new List<string> { "*" };
        public bool AnalyzeTracks = true;
        public int Seed = 7;

        /// <summary>Папка, относительно которой разрешаются пути (там лежит conveer.json).</summary>
        public string BaseDir = Directory.GetCurrentDirectory();

        public float Sec(string key) => Seconds.TryGetValue(key, out var v) ? v : 10f;

        public string GameDir => Full(GameRoot);
        public string OutDir => Full(Output);
        public string MusicDir => Path.Combine(GameDir, "Assets", "Resources", "Music");

        private string Full(string p) => Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(BaseDir, p));

        public static Config Load(string path)
        {
            var c = new Config();
            if (path == null)
            {
                // conveer.json ищется в текущей папке и выше – запуск из bin/ тоже работает.
                var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
                while (dir != null && !File.Exists(Path.Combine(dir.FullName, "conveer.json"))) dir = dir.Parent;
                if (dir == null) return c;
                path = Path.Combine(dir.FullName, "conveer.json");
            }
            path = Path.GetFullPath(path);
            c.BaseDir = Path.GetDirectoryName(path);
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            var r = doc.RootElement;
            c.GameRoot = Str(r, "gameRoot", c.GameRoot);
            c.Output = Str(r, "output", c.Output);
            c.Ffmpeg = Str(r, "ffmpeg", c.Ffmpeg);
            c.Ffprobe = Str(r, "ffprobe", c.Ffprobe);
            c.Width = Int(r, "width", c.Width);
            c.Height = Int(r, "height", c.Height);
            c.Fps = Int(r, "fps", c.Fps);
            c.Crf = Int(r, "crf", c.Crf);
            c.Preset = Str(r, "preset", c.Preset);
            c.Subtitles = Str(r, "subtitles", c.Subtitles);
            c.Language = Str(r, "language", c.Language);
            c.AnalyzeTracks = r.TryGetProperty("analyzeTracks", out var a) ? a.GetBoolean() : c.AnalyzeTracks;
            c.Seed = Int(r, "seed", c.Seed);
            if (r.TryGetProperty("volumes", out var v))
            {
                c.MasterVolume = Flt(v, "master", c.MasterVolume);
                c.MusicVolume = Flt(v, "music", c.MusicVolume);
                c.SfxVolume = Flt(v, "sfx", c.SfxVolume);
            }
            if (r.TryGetProperty("seconds", out var s))
                foreach (var p in s.EnumerateObject()) c.Seconds[p.Name] = (float)p.Value.GetDouble();
            if (r.TryGetProperty("scenes", out var sc))
            {
                c.Scenes.Clear();
                foreach (var x in sc.EnumerateArray()) c.Scenes.Add(x.GetString());
            }
            return c;
        }

        private static string Str(JsonElement e, string k, string d) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : d;
        private static int Int(JsonElement e, string k, int d) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : d;
        private static float Flt(JsonElement e, string k, float d) => e.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.Number ? (float)v.GetDouble() : d;

        /// <summary>Подходит ли сцена под шаблоны (точное имя, «boss_*», «*»).</summary>
        public static bool Matches(string id, IEnumerable<string> patterns)
        {
            foreach (var p in patterns)
            {
                if (p == "*" || p == id) return true;
                if (p.EndsWith("*") && id.StartsWith(p.Substring(0, p.Length - 1), StringComparison.Ordinal)) return true;
            }
            return false;
        }
    }
}
