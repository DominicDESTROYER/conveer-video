using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Tokenfall.Core;

namespace Conveer
{
    /// <summary>
    /// Конвейер видео TOKENFALL.
    ///   dotnet run -c Release --                     — все сцены из conveer.json ("scenes")
    ///   dotnet run -c Release -- boss_* death        — только выбранные (имя или шаблон с *)
    ///   dotnet run -c Release -- list                — список сцен
    ///   dotnet run -c Release -- tracks              — только проверка треков и отчёт
    ///   --config путь/conveer.json   --quick (640×360, 24 к/с — быстрая проверка)   --no-analyze
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            Console.OutputEncoding = System.Text.Encoding.UTF8;
            string configPath = null;
            bool quick = false, noAnalyze = false;
            var rest = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) configPath = args[++i];
                else if (args[i] == "--quick") quick = true;
                else if (args[i] == "--no-analyze") noAnalyze = true;
                else rest.Add(args[i]);
            }
            var cfg = Config.Load(configPath);
            if (quick)
            {
                cfg.Width = 640;
                cfg.Height = 360;
                cfg.Fps = 24;
                cfg.Preset = "ultrafast";
            }
            if (noAnalyze) cfg.AnalyzeTracks = false;
            Loc.Current = cfg.Language == "en" ? Language.English : Language.Russian;

            if (!Directory.Exists(Path.Combine(cfg.GameDir, "Assets", "Scripts")))
            {
                Console.Error.WriteLine($"Не найдена игра: {cfg.GameDir}. Положите game-token рядом с conveer-video или поправьте gameRoot в conveer.json.");
                return 2;
            }
            var all = Studio.All(cfg);
            if (rest.Count > 0 && rest[0] == "list")
            {
                foreach (var s in all) Console.WriteLine($"{s.Id,-22} {s.Title}");
                return 0;
            }

            var ff = new Ffmpeg(cfg);
            string ffVersion = ff.Version();
            if (ffVersion == null)
            {
                Console.Error.WriteLine($"Не найден ffmpeg ({cfg.Ffmpeg}). Установите ffmpeg (с libx264) и/или укажите путь в conveer.json → \"ffmpeg\".");
                return 3;
            }
            Console.WriteLine("Игра:   " + cfg.GameDir);
            Console.WriteLine("Музыка: " + cfg.MusicDir + (Directory.Exists(cfg.MusicDir) ? "" : "  (папки нет — везде прозвучит процедурная музыка)"));
            Console.WriteLine("Вывод:  " + cfg.OutDir);
            Console.WriteLine(ffVersion + (ff.HasLibass ? "" : "  (без libass: субтитры роликов будут отдельной дорожкой)"));

            bool tracksOnly = rest.Count > 0 && rest[0] == "tracks";
            var patterns = tracksOnly ? new List<string>() : rest.Count > 0 ? rest : cfg.Scenes;
            var selected = all.Where(s => Config.Matches(s.Id, patterns)).ToList();
            if (!tracksOnly && selected.Count == 0)
            {
                Console.Error.WriteLine("Ни одна сцена не подошла под: " + string.Join(" ", patterns) + ". Список: dotnet run -- list");
                return 1;
            }

            string videos = Path.Combine(cfg.OutDir, "videos");
            string tmp = Path.Combine(cfg.OutDir, "tmp");
            string logs = Path.Combine(cfg.OutDir, "logs");
            Directory.CreateDirectory(videos);
            Directory.CreateDirectory(tmp);
            Directory.CreateDirectory(logs);

            Console.WriteLine("Проверка треков…");
            var tracks = Report.AnalyzeTracks(cfg, ff);
            // Одинаковые замечания (например, «MP3 внутри .ogg» у всех файлов) — одной строкой: треки и их числа.
            var num = new System.Text.RegularExpressions.Regex(@"(?<![A-Za-z])[−-]?\d+(?:[.,]\d+)?\s*(?:с|LUFS|dBTP)?");
            foreach (var g in tracks.SelectMany(t => t.Warnings.Select(w => (t.File, w))).GroupBy(x => num.Replace(x.w, "#")))
            {
                var first = g.First().w;
                if (g.Count() <= 2)
                {
                    foreach (var x in g) Console.WriteLine($"  {x.File}: {x.w}");
                    continue;
                }
                string head = num.IsMatch(first) ? first.Substring(0, num.Match(first).Index).Trim() : first.Split('(')[0].Trim();
                var items = g.Select(x => num.IsMatch(x.w) ? $"{x.File} ({num.Match(x.w).Value.Trim()})" : x.File);
                Console.WriteLine($"  {head} — {g.Count()} трек(ов): {string.Join(", ", items)}");
            }

            var results = new List<SceneResult>();
            var lib = new DiskLibrary(cfg.MusicDir, ff);
            var studio = new Studio(cfg);
            var total = Stopwatch.StartNew();
            int index = 0;
            foreach (var scene in selected)
            {
                index++;
                var sw = Stopwatch.StartNew();
                var r = new SceneResult { Scene = scene };
                Console.Write($"[{index}/{selected.Count}] {scene.Id} — {scene.Title} … ");
                try
                {
                    var s = new Session(scene.Id, cfg, ff, lib, tmp);
                    scene.Shoot(s, studio);
                    r.Video = s.Finish(videos);
                    r.Seconds = s.Seconds;
                    r.Log = s.Log;
                    r.Heard = s.Heard;
                    r.Audio = s.AudioStats;
                    File.WriteAllLines(Path.Combine(logs, scene.Id + ".txt"), s.Log);
                }
                catch (Exception e)
                {
                    r.Error = e.Message;
                    Console.Error.WriteLine("\n  ошибка: " + e);
                }
                r.RenderSeconds = sw.Elapsed.TotalSeconds;
                r.Save(logs);
                results.Add(r);
                var probs = Report.Problems(r, cfg);
                Console.WriteLine($"{r.Seconds:0} с видео за {r.RenderSeconds:0} с" + (probs.Count > 0 ? "  ⚠ " + string.Join("; ", probs) : ""));
            }
            try { Directory.Delete(tmp, true); } catch (Exception) { }
            // Отчёт — по всем записанным сценам: и этого запуска, и прошлых (если писали только часть).
            Report.Write(cfg, tracks, SceneResult.LoadAll(logs, all), ffVersion);
            Console.WriteLine($"Готово за {total.Elapsed.TotalMinutes:0.0} мин. Отчёт: {Path.Combine(cfg.OutDir, "index.html")}");
            return results.Any(r => r.Error != null) ? 1 : 0;
        }
    }
}
