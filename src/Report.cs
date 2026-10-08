using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using Tokenfall.Art.Cinema;
using Tokenfall.Core.Content;

namespace Conveer
{
    /// <summary>Результат одной сцены для отчёта (сохраняется в out/logs/&lt;сцена&gt;.json, чтобы отчёт собирал все записанные сцены).</summary>
    public sealed class SceneResult
    {
        public SceneDef Scene;
        public string Video;
        public float Seconds;
        public double RenderSeconds;
        public List<string> Log;
        public SortedSet<string> Heard;
        public AudioMixer.Stats Audio;
        public string Error;
        public DateTime When = DateTime.Now;

        private sealed class Dto
        {
            public string Id { get; set; }
            public string Video { get; set; }
            public float Seconds { get; set; }
            public double RenderSeconds { get; set; }
            public List<string> Log { get; set; }
            public List<string> Heard { get; set; }
            public float Peak { get; set; }
            public double Clipped { get; set; }
            public string Error { get; set; }
            public DateTime When { get; set; }
        }

        public void Save(string dir)
        {
            var d = new Dto
            {
                Id = Scene.Id, Video = Video, Seconds = Seconds, RenderSeconds = RenderSeconds, Log = Log, Heard = Heard?.ToList(),
                Peak = Audio?.Peak ?? 0f, Clipped = Audio?.ClippedPercent ?? 0, Error = Error, When = When,
            };
            File.WriteAllText(Path.Combine(dir, Scene.Id + ".json"), System.Text.Json.JsonSerializer.Serialize(d, new System.Text.Json.JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        }

        /// <summary>Все сохранённые результаты сцен из каталога (в порядке каталога сцен).</summary>
        public static List<SceneResult> LoadAll(string dir, List<SceneDef> scenes)
        {
            var list = new List<SceneResult>();
            foreach (var sc in scenes)
            {
                string p = Path.Combine(dir, sc.Id + ".json");
                if (!File.Exists(p)) continue;
                try
                {
                    var d = System.Text.Json.JsonSerializer.Deserialize<Dto>(File.ReadAllText(p));
                    list.Add(new SceneResult
                    {
                        Scene = sc, Video = d.Video, Seconds = d.Seconds, RenderSeconds = d.RenderSeconds, Log = d.Log,
                        Heard = d.Heard != null ? new SortedSet<string>(d.Heard, StringComparer.Ordinal) : null,
                        Audio = new AudioMixer.Stats { Peak = d.Peak, ClippedPercent = d.Clipped }, Error = d.Error, When = d.When,
                    });
                }
                catch (Exception)
                {
                }
            }
            return list;
        }
    }

    /// <summary>Проверка треков и отчёт: out/index.html (видео подряд с журналом музыки) и out/report.md.</summary>
    public static class Report
    {
        public sealed class TrackInfo
        {
            public string File;
            public string Group;
            public string Path;
            public double Duration;
            public string Codec;
            public double Lufs = double.NaN, Peak = double.NaN;
            public double Intro = double.NaN, Tail = double.NaN;   // тихие вступление и конец, с
            public string Plays;                 // что звучит вместо отсутствующего файла
            public readonly List<string> Warnings = new List<string>();
        }

        public static List<TrackInfo> AnalyzeTracks(Config cfg, Ffmpeg ff)
        {
            var list = new List<TrackInfo>();
            foreach (var t in MusicCatalog.Tracks)
            {
                var ti = new TrackInfo { File = t.File, Group = t.Group.ToString(), Path = DiskLibrary.FindFile(cfg.MusicDir, t.File) };
                if (ti.Path == null)
                {
                    string slot = t.Fallback;
                    var seen = new HashSet<string>();
                    while (slot != null && seen.Add(slot) && DiskLibrary.FindFile(cfg.MusicDir, slot) == null) slot = MusicCatalog.Get(slot)?.Fallback;
                    ti.Plays = slot != null && DiskLibrary.FindFile(cfg.MusicDir, slot) != null ? slot : "процедурная петля";
                    ti.Warnings.Add("нет файла — звучит " + ti.Plays);
                    list.Add(ti);
                    continue;
                }
                ti.Duration = ff.Duration(ti.Path);
                ti.Codec = ff.Codec(ti.Path);
                string ext = System.IO.Path.GetExtension(ti.Path).TrimStart('.').ToLowerInvariant();
                string codec = ti.Codec.Split(',')[0];
                if (ext == "ogg" && codec != "vorbis" && codec != "opus")
                    ti.Warnings.Add($"внутри .ogg — {codec.ToUpperInvariant()} (Unity читает; шов петли сглаживает кроссфейд, но лучше настоящий OGG/WAV)");
                if (cfg.AnalyzeTracks)
                {
                    var (lufs, peak) = ff.Loudness(ti.Path);
                    ti.Lufs = lufs;
                    ti.Peak = peak;
                    if (!double.IsNaN(lufs) && (lufs > -11 || lufs < -22)) ti.Warnings.Add($"громкость {lufs:0.0} LUFS (рекомендуется около −16)");
                    if (!double.IsNaN(peak) && peak > 1.0) ti.Warnings.Add($"пик {peak:0.0} dBTP — выше 0 дБ, при импорте возможны щелчки (рекомендуется ≤ −1)");
                }
                if (cfg.AnalyzeTracks && t.Group != MusicCatalog.Group.Sting) EnvelopeChecks(ff, t, ti);
                if (t.Group == MusicCatalog.Group.Sting && ti.Duration > 15)
                    ti.Warnings.Add($"джингл {ti.Duration:0} с — в игре он короткий: уходит, как только вступает музыка или игрок покидает экран (рекомендуется 3–12 с)");
                if (t.Group == MusicCatalog.Group.Cinema)
                {
                    float need = CinemaLength(t.File);
                    if (need > 0 && ti.Duration < need) ti.Warnings.Add($"трек {ti.Duration:0.0} с короче ролика ({need:0.0} с) — в конце он начнётся заново");
                }
                if ((t.Group == MusicCatalog.Group.Background || t.Group == MusicCatalog.Group.Menu || t.Group == MusicCatalog.Group.Boss) && ti.Duration < 60)
                    ti.Warnings.Add($"всего {ti.Duration:0} с — петля будет часто повторяться");
                list.Add(ti);
            }

            // Файлы, которых игра не знает (не трек каталога, не вариант, не личный трек биома/босса/клетки).
            if (Directory.Exists(cfg.MusicDir))
            {
                var known = new HashSet<string>(MusicCatalog.Tracks.Select(x => x.File));
                foreach (var b in BiomeDatabase.Biomes) known.Add(b.Music);
                foreach (var b in BossDatabase.All) known.Add("boss_" + b.Key);
                for (int i = 1; i <= 10; i++) known.Add("cine_cell_" + i);
                foreach (var f in Directory.GetFiles(cfg.MusicDir))
                {
                    if (f.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
                    string name = System.IO.Path.GetFileNameWithoutExtension(f);
                    string bare = System.Text.RegularExpressions.Regex.Replace(name, "_[2-9]$", "");
                    if (known.Contains(name) || known.Contains(bare)) continue;
                    var ti = new TrackInfo { File = name, Group = "?", Path = f };
                    ti.Warnings.Add("игра не использует этот файл (нет такого трека в MusicCatalog)");
                    list.Add(ti);
                }
            }
            return list;
        }

        /// <summary>
        /// Тихое вступление и долгое затухание: окна 0,5 с тише средней громкости трека на 12 дБ.
        /// Бою с боссом нужно сразу звучать, а петле — не проваливаться на шве.
        /// </summary>
        private static void EnvelopeChecks(Ffmpeg ff, MusicCatalog.Track t, TrackInfo ti)
        {
            var env = ff.Envelope(ti.Path);
            if (env.Count < 20) return;
            var sorted = env.OrderBy(x => x).ToList();
            double median = sorted[sorted.Count / 2], quiet = median - 12;
            int first = env.FindIndex(x => x >= quiet), last = env.FindLastIndex(x => x >= quiet);
            if (first < 0) return;
            double intro = first * 0.5, tail = (env.Count - 1 - last) * 0.5;
            ti.Intro = intro;
            ti.Tail = tail;
            if (t.Group == MusicCatalog.Group.Boss && intro > 2)
                ti.Warnings.Add($"тихое вступление {intro:0.#} с — бой с боссом начнётся почти в тишине (рекомендуется ≤ 2 с)");
            else if ((t.Group == MusicCatalog.Group.Background || t.Group == MusicCatalog.Group.Menu) && intro > 6)
                ti.Warnings.Add($"тихое вступление {intro:0.#} с — после смены биома или экрана сначала почти тишина");
            if (t.Group != MusicCatalog.Group.Cinema && tail > 6)
                ti.Warnings.Add($"затухание в конце {tail:0.#} с — на шве петли будет провал громкости (игра сводит за 3 с)");
        }

        private static float CinemaLength(string slot)
        {
            float max = 0f;
            foreach (var id in CineCatalog.AllIds())
            {
                var s = CineCatalog.Get(id);
                if (MusicCatalog.Slot(s.Music, s.Chapter) == slot) max = Math.Max(max, s.Duration);
            }
            return max;
        }

        private static bool HeardTrack(SortedSet<string> heard, string slot) =>
            heard.Any(h => h == slot || (h.StartsWith(slot + "_") && h.Length == slot.Length + 2 && char.IsDigit(h[h.Length - 1])));

        /// <summary>Чего не хватило в сцене: ожидаемые треки, которые не прозвучали файлом.</summary>
        public static List<string> Problems(SceneResult r, Config cfg)
        {
            var p = new List<string>();
            if (r.Error != null) p.Add("ошибка: " + r.Error);
            if (r.Heard != null)
                foreach (var e in r.Scene.Expect)
                    if (!HeardTrack(r.Heard, e))
                        p.Add(DiskLibrary.FindFile(cfg.MusicDir, e) == null ? $"{e}: файла нет (звучал запасной трек)" : $"{e}: не прозвучал");
            if (r.Audio != null && r.Audio.ClippedPercent > 0.01) p.Add($"перегруз сведения: {r.Audio.ClippedPercent:0.00}% сэмплов (пик {r.Audio.Peak:0.00})");
            return p;
        }

        public static void Write(Config cfg, List<TrackInfo> tracks, List<SceneResult> results, string ffVersion)
        {
            string outDir = cfg.OutDir;
            var md = new StringBuilder();
            md.AppendLine("# Отчёт конвейера видео TOKENFALL");
            md.AppendLine();
            md.AppendLine($"Создан: {DateTime.Now:yyyy-MM-dd HH:mm}. Игра: `{cfg.GameDir}`. {ffVersion}");
            md.AppendLine();
            md.AppendLine("## Треки");
            md.AppendLine();
            md.AppendLine("| Файл | Группа | Длина | Кодек | LUFS | Пик dBTP | Тихое начало / конец, с | Замечания |");
            md.AppendLine("|---|---|---|---|---|---|---|---|");
            foreach (var t in tracks)
                md.AppendLine($"| `{t.File}` | {t.Group} | {(t.Path == null ? "—" : Time(t.Duration))} | {t.Codec ?? "—"} | {Num(t.Lufs)} | {Num(t.Peak)} | {Num(t.Intro)} / {Num(t.Tail)} | {string.Join("; ", t.Warnings)} |");
            md.AppendLine();
            md.AppendLine("## Видео");
            md.AppendLine();
            foreach (var r in results)
            {
                md.AppendLine($"### {r.Scene.Title} — `{r.Scene.Id}`");
                md.AppendLine();
                md.AppendLine(r.Scene.Description);
                md.AppendLine();
                if (r.Video != null) md.AppendLine($"Видео: [videos/{r.Scene.Id}.mp4](videos/{r.Scene.Id}.mp4) · {Time(r.Seconds)} · записано {r.When:yyyy-MM-dd HH:mm}");
                if (r.Heard != null) md.AppendLine("Прозвучало: " + string.Join(", ", r.Heard.Select(h => "`" + h + "`")));
                var probs = Problems(r, cfg);
                md.AppendLine(probs.Count == 0 ? "Проверка: всё ожидаемое прозвучало." : "Проверка: " + string.Join("; ", probs));
                md.AppendLine();
                if (r.Log != null && r.Log.Count > 0)
                {
                    md.AppendLine("```text");
                    foreach (var l in r.Log) md.AppendLine(l);
                    md.AppendLine("```");
                    md.AppendLine();
                }
            }
            File.WriteAllText(Path.Combine(outDir, "report.md"), md.ToString(), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(outDir, "index.html"), Html(cfg, tracks, results, ffVersion), new UTF8Encoding(false));
        }

        private static string Time(double s) => double.IsNaN(s) ? "—" : $"{(int)(s / 60)}:{(int)(s % 60):00}";
        private static string Num(double v) => double.IsNaN(v) ? "—" : v.ToString("0.0", CultureInfo.InvariantCulture);
        private static string H(string s) => WebUtility.HtmlEncode(s ?? "");

        private static string Html(Config cfg, List<TrackInfo> tracks, List<SceneResult> results, string ffVersion)
        {
            var sb = new StringBuilder();
            sb.Append(@"<!doctype html><html lang=""ru""><head><meta charset=""utf-8""><meta name=""viewport"" content=""width=device-width,initial-scale=1"">
<title>TOKENFALL · проверка музыки</title><style>
:root{--bg:#0e0f1a;--panel:#171a2b;--ink:#e8ecff;--dim:#9aa3c7;--ok:#5fe08a;--warn:#ffb340;--bad:#ff5a6e;--accent:#39d0ff}
body{margin:0;background:var(--bg);color:var(--ink);font:15px/1.5 system-ui,Segoe UI,Roboto,sans-serif}
main{max-width:1180px;margin:0 auto;padding:24px 16px 80px}h1{margin:0 0 4px}h2{margin:36px 0 12px;color:var(--accent)}
.meta{color:var(--dim);font-size:13px}nav{display:flex;flex-wrap:wrap;gap:6px;margin:16px 0}nav a{background:var(--panel);color:var(--ink);text-decoration:none;padding:3px 9px;border-radius:6px;font-size:13px}
nav a.bad{box-shadow:inset 0 0 0 1px var(--warn)}table{width:100%;border-collapse:collapse;font-size:13px}td,th{border-bottom:1px solid #262a40;padding:5px 6px;text-align:left;vertical-align:top}
th{color:var(--dim);font-weight:600}code{font-family:ui-monospace,Consolas,monospace;font-size:12.5px}.w{color:var(--warn)}.ok{color:var(--ok)}
section.scene{background:var(--panel);border-radius:12px;padding:16px;margin:18px 0}section.scene h3{margin:0 0 4px}
video{width:100%;border-radius:8px;background:#000;margin:10px 0}details{margin-top:8px}pre{white-space:pre-wrap;color:var(--dim);font-size:12px;margin:6px 0 0}
</style></head><body><main>");
            sb.Append("<h1>TOKENFALL · проверка музыки в видео</h1>");
            sb.Append($"<div class=meta>{DateTime.Now:yyyy-MM-dd HH:mm} · игра: <code>{H(cfg.GameDir)}</code> · {H(ffVersion)}</div>");
            sb.Append("<nav>");
            foreach (var r in results)
                sb.Append($"<a href=\"#{H(r.Scene.Id)}\"{(Problems(r, cfg).Count > 0 ? " class=bad" : "")}>{H(r.Scene.Id)}</a>");
            sb.Append("</nav>");
            foreach (var r in results)
            {
                var probs = Problems(r, cfg);
                sb.Append($"<section class=scene id=\"{H(r.Scene.Id)}\"><h3>{H(r.Scene.Title)}</h3><div class=meta><code>{H(r.Scene.Id)}</code> · {Time(r.Seconds)} · записано {r.When:yyyy-MM-dd HH:mm}</div>");
                sb.Append($"<p>{H(r.Scene.Description)}</p>");
                if (r.Video != null) sb.Append($"<video controls preload=\"none\" src=\"videos/{H(r.Scene.Id)}.mp4\"></video>");
                if (r.Heard != null) sb.Append("<div>Прозвучало: " + string.Join(", ", r.Heard.Select(h => "<code>" + H(h) + "</code>")) + "</div>");
                sb.Append(probs.Count == 0 ? "<div class=ok>Всё ожидаемое прозвучало.</div>" : "<div class=w>" + string.Join("<br>", probs.Select(H)) + "</div>");
                if (r.Log != null && r.Log.Count > 0) sb.Append("<details><summary>Журнал музыки</summary><pre>" + H(string.Join("\n", r.Log)) + "</pre></details>");
                sb.Append("</section>");
            }
            sb.Append("<h2>Треки</h2><table><tr><th>Файл</th><th>Группа</th><th>Длина</th><th>Кодек</th><th>LUFS</th><th>Пик</th><th>Тихо в начале / конце, с</th><th>Замечания</th></tr>");
            foreach (var t in tracks)
                sb.Append($"<tr><td><code>{H(t.File)}</code></td><td>{H(t.Group)}</td><td>{(t.Path == null ? "—" : Time(t.Duration))}</td><td>{H(t.Codec ?? "—")}</td><td>{Num(t.Lufs)}</td><td>{Num(t.Peak)}</td><td>{Num(t.Intro)} / {Num(t.Tail)}</td><td class=w>{H(string.Join("; ", t.Warnings))}</td></tr>");
            sb.Append("</table></main></body></html>");
            return sb.ToString();
        }
    }
}
