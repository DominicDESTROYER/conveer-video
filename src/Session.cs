using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Tokenfall.Art;
using Tokenfall.Art.Audio;
using Tokenfall.Art.Cinema;
using Tokenfall.Core;
using Tokenfall.Core.Simulation;
using UnityEngine;

namespace Conveer
{
    /// <summary>
    /// Одно видео: такт симуляции 60 Гц, режиссёр музыки игры (MusicDirector + MusicFlow), сведение звука,
    /// кадры в ffmpeg, субтитры роликов и журнал музыки. Сцены (Scenes.cs) управляют им шагами.
    /// </summary>
    public sealed class Session : IDisposable
    {
        public const float Dt = 1f / 60f;

        public readonly string Id;
        public readonly Config Cfg;
        public readonly MusicDirector Music;
        public readonly MusicFlow Flow;
        public readonly Frame Frame;
        public readonly List<string> Log = new List<string>();
        public readonly SortedSet<string> Heard = new SortedSet<string>(StringComparer.Ordinal);
        public readonly List<(float start, float end, string speaker, string text, Rgba color)> Subs = new List<(float, float, string, string, Rgba)>();
        public AudioMixer.Stats AudioStats;

        private readonly AudioMixer _mixer;
        private readonly Ffmpeg _ff;
        private readonly Ffmpeg.VideoWriter _video;
        private readonly string _tmp;
        private float _t;
        private float _nextFrame;
        private int _frames;

        /// <summary>Время видео, с.</summary>
        public float T => _t;

        public Session(string id, Config cfg, Ffmpeg ff, DiskLibrary lib, string tmpDir)
        {
            Id = id;
            Cfg = cfg;
            _ff = ff;
            _tmp = tmpDir;
            Music = new MusicDirector(lib, cfg.Seed);
            Music.Trace = (t, msg) => Log.Add(Stamp(t) + "  " + msg);
            Flow = new MusicFlow(Music);
            _mixer = new AudioMixer(lib, Music, cfg);
            Frame = new Frame(cfg.Width, cfg.Height);
            _video = new Ffmpeg.VideoWriter(ff, Path.Combine(tmpDir, id + ".video.mp4"), cfg.Width, cfg.Height, null);
        }

        public static string Stamp(float t)
        {
            int ds = (int)Math.Round(Math.Max(0f, t) * 10f);
            return (ds / 600).ToString("00") + ":" + (ds % 600 / 10).ToString("00") + "." + (ds % 10);
        }

        public void Note(string what) => Log.Add(Stamp(_t) + "  – " + what);

        public void Sfx(string key) => _mixer.Sfx(key);

        /// <summary>
        /// Такт 1/60 с: логика сцены, шаг режиссёра музыки, сведение звука; когда подходит время кадра –
        /// draw рисует кадр, поверх – панель музыки, кадр уходит в ffmpeg.
        /// </summary>
        public void Tick(Action logic, Action<Frame> draw)
        {
            logic?.Invoke();
            Music.Update(Dt);
            _mixer.Advance(Dt);
            foreach (var v in Music.Voices)
                if (v.Volume > 0.05f) Heard.Add(v.Clip.Name);
            if (Music.Sting.Volume > 0.05f) Heard.Add(Music.Sting.Clip.Name);
            _t += Dt;
            while (_t >= _nextFrame)
            {
                draw?.Invoke(Frame);
                Overlay();
                _video.Write(Frame);
                _frames++;
                _nextFrame += 1f / Cfg.Fps;
            }
        }

        public void Wait(float seconds, Action logic, Action<Frame> draw)
        {
            int n = (int)Math.Round(seconds / Dt);
            for (int i = 0; i < n; i++) Tick(logic, draw);
        }

        // ───────────── Панель музыки ─────────────

        private void Overlay()
        {
            int s = Math.Max(1, Frame.H / 240);
            var f = Frame;
            string head = Id.ToUpperInvariant() + "  " + Stamp(_t);
            // Справа внизу: сверху по центру – полоса здоровья босса и подписи сцен.
            f.Text(head, f.W - Frame.TextWidth(head, s) - 6 * s, f.H - 10 * s, s, new Color(0.75f, 0.9f, 1f));

            var lines = new List<(string text, Color c, float vol)>();
            foreach (var v in Music.Voices)
            {
                if (v.Clip == null) continue;
                bool cur = v == Music.Current;
                string state = !v.Playing ? "WAIT " + v.Delay.ToString("0.0", CultureInfo.InvariantCulture) + "S" : cur ? (v.Gain < 0.999f ? "IN" : "") : "OUT";
                string name = v.Clip.IsFile ? v.Clip.Name : v.Clip.Name.Replace("synth:", "SYNTH ");
                lines.Add(("> " + name + "  " + Stamp(v.Time) + "/" + Stamp(v.Clip.Length) + "  " + state,
                    cur ? new Color(0.45f, 0.95f, 1f) : new Color(0.65f, 0.65f, 0.75f), v.Volume));
            }
            if (Music.Sting.Clip != null)
                lines.Add(("* " + Music.Sting.Clip.Name + "  " + Stamp(Music.Sting.Time) + (Music.Sting.Target <= 0f ? "  OUT" : ""), new Color(1f, 0.82f, 0.3f), Music.Sting.Volume));
            if (lines.Count == 0) lines.Add(("> SILENCE", new Color(0.6f, 0.6f, 0.7f), 0f));

            int y = f.H - (lines.Count * 9 + 6) * s;
            f.Fill(4 * s, y - 4 * s, Math.Min(f.W - 8 * s, 250 * s), lines.Count * 9 * s + 6 * s, 0.01f, 0.01f, 0.04f, 0.55f);
            foreach (var l in lines)
            {
                f.Fill(8 * s, y, 30 * s, 5 * s, 0.15f, 0.15f, 0.2f);
                f.Fill(8 * s, y, (int)(30 * s * Math.Min(1f, l.vol)), 5 * s, l.c.r, l.c.g, l.c.b);
                f.Text(l.text, 42 * s, y, s, l.c);
                y += 9 * s;
            }
        }

        // ───────────── Субтитры ─────────────

        public void AddSubtitles(Cutscene scene, float offset)
        {
            foreach (var line in scene.Lines)
            {
                string text = line.Args == null ? Loc.T(line.Text) : Loc.F(line.Text, line.Args.Select(a => a is string str ? Loc.T(str) : a).ToArray());
                Subs.Add((offset + line.Start, offset + line.End, Loc.T(line.Speaker).ToUpperInvariant(), text, line.Color));
            }
        }

        private static string AssTime(float t) => $"{(int)(t / 3600)}:{(int)(t / 60) % 60:00}:{(t % 60):00.00}".Replace(',', '.');

        private static string SrtTime(float t) => $"{(int)(t / 3600):00}:{(int)(t / 60) % 60:00}:{(int)(t % 60):00},{(int)((t * 1000) % 1000):000}";

        private string WriteAss()
        {
            var sb = new StringBuilder();
            sb.AppendLine("[Script Info]\nScriptType: v4.00+\nPlayResX: " + Cfg.Width + "\nPlayResY: " + Cfg.Height + "\nWrapStyle: 0\n");
            sb.AppendLine("[V4+ Styles]");
            sb.AppendLine("Format: Name, Fontname, Fontsize, PrimaryColour, SecondaryColour, OutlineColour, BackColour, Bold, Italic, Underline, StrikeOut, ScaleX, ScaleY, Spacing, Angle, BorderStyle, Outline, Shadow, Alignment, MarginL, MarginR, MarginV, Encoding");
            int size = Cfg.Height * 30 / 720;
            sb.AppendLine($"Style: Default,Arial,{size},&H00FFFFFF,&H00FFFFFF,&H00100808,&H90100808,0,0,0,0,100,100,0,0,3,{Math.Max(2, size / 6)},0,2,60,60,{Cfg.Height / 12},204\n");
            sb.AppendLine("[Events]\nFormat: Layer, Start, End, Style, Name, MarginL, MarginR, MarginV, Effect, Text");
            foreach (var s in Subs)
            {
                string col = $"&H{s.color.B:X2}{s.color.G:X2}{s.color.R:X2}&";
                string text = s.text.Replace("\n", "\\N");
                sb.AppendLine($"Dialogue: 0,{AssTime(s.start)},{AssTime(s.end)},Default,,0,0,0,,{{\\c{col}\\b1}}{s.speaker}{{\\b0\\c&HFFFFFF&}}\\N{text}");
            }
            string path = Path.Combine(_tmp, Id + ".ass");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        private string WriteSrt()
        {
            var sb = new StringBuilder();
            int i = 1;
            foreach (var s in Subs)
                sb.Append(i++).Append('\n').Append(SrtTime(s.start)).Append(" --> ").Append(SrtTime(s.end)).Append('\n')
                    .Append(s.speaker).Append(": ").Append(s.text).Append("\n\n");
            string path = Path.Combine(_tmp, Id + ".srt");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
            return path;
        }

        // ───────────── Готовое видео ─────────────

        /// <summary>Закрыть видеопоток, записать звук, свести в итоговый MP4 (с субтитрами роликов).</summary>
        public string Finish(string videosDir)
        {
            _video.Dispose();
            string wav = Path.Combine(_tmp, Id + ".wav");
            AudioStats = _mixer.WriteWav(wav);
            string output = Path.Combine(videosDir, Id + ".mp4");
            string mode = Subs.Count == 0 ? "off" : Cfg.Subtitles;
            if (mode == "burn" && !_ff.HasLibass) mode = "soft";
            string srt = mode == "soft" || mode == "burn" ? WriteSrt() : null;
            string ass = mode == "burn" ? WriteAss() : null;
            _ff.Mux(Path.Combine(_tmp, Id + ".video.mp4"), wav, srt, ass, output);
            File.Delete(wav);
            File.Delete(Path.Combine(_tmp, Id + ".video.mp4"));
            return output;
        }

        public float Seconds => _t;
        public int Frames => _frames;

        public void Dispose()
        {
        }
    }
}
