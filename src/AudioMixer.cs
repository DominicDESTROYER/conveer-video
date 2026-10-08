using System;
using System.Collections.Generic;
using System.IO;
using Tokenfall.Art.Audio;

namespace Conveer
{
    /// <summary>
    /// Треки для режиссёра музыки с диска: файлы Assets/Resources/Music игры (как Resources.Load: имя без расширения,
    /// варианты name_2 … name_9) и процедурные петли ChipSynth. PCM декодирует ffmpeg; в памяти — несколько последних треков.
    /// </summary>
    public sealed class DiskLibrary : IMusicLibrary
    {
        public sealed class Track : IMusicClip
        {
            public string Name { get; set; }
            public float Length { get; set; }
            public bool IsFile { get; set; }
            public string Path;
            public string SynthKey;
            public int SynthChapter;
        }

        private static readonly string[] Extensions = { ".ogg", ".wav", ".mp3", ".aif", ".aiff", ".flac" };
        private const int MaxVariants = 9;
        private readonly string _dir;
        private readonly Ffmpeg _ff;
        private readonly Dictionary<string, IReadOnlyList<IMusicClip>> _variants = new Dictionary<string, IReadOnlyList<IMusicClip>>();
        private readonly Dictionary<string, Track> _synth = new Dictionary<string, Track>();
        private readonly Dictionary<IMusicClip, float[]> _pcm = new Dictionary<IMusicClip, float[]>();
        private readonly LinkedList<IMusicClip> _lru = new LinkedList<IMusicClip>();

        /// <summary>Какие файлы реально прозвучали (для отчёта).</summary>
        public readonly HashSet<string> Used = new HashSet<string>();

        public DiskLibrary(string musicDir, Ffmpeg ff)
        {
            _dir = musicDir;
            _ff = ff;
        }

        public static string FindFile(string dir, string name)
        {
            foreach (var ext in Extensions)
            {
                var p = System.IO.Path.Combine(dir, name + ext);
                if (File.Exists(p)) return p;
            }
            return null;
        }

        public IReadOnlyList<IMusicClip> Variants(string name)
        {
            if (_variants.TryGetValue(name, out var v)) return v;
            var list = new List<IMusicClip>();
            for (int i = 1; i <= MaxVariants; i++)
            {
                string file = i == 1 ? name : name + "_" + i;
                var p = Directory.Exists(_dir) ? FindFile(_dir, file) : null;
                if (p == null)
                {
                    if (i > 1) break;
                    continue;
                }
                list.Add(new Track { Name = file, Path = p, Length = (float)_ff.Duration(p), IsFile = true });
            }
            _variants[name] = list;
            return list;
        }

        public IMusicClip Procedural(string key, int chapter)
        {
            string id = key + "#" + chapter;
            if (_synth.TryGetValue(id, out var t)) return t;
            var data = ChipSynth.Music(key, chapter, AudioMixer.Rate);
            t = new Track { Name = "synth:" + key, Length = data.Length / (float)AudioMixer.Rate, IsFile = false, SynthKey = key, SynthChapter = chapter };
            _synth[id] = t;
            return t;
        }

        /// <summary>Стерео PCM 44.1 кГц (чередование L/R).</summary>
        public float[] Pcm(IMusicClip clip)
        {
            if (_pcm.TryGetValue(clip, out var data))
            {
                _lru.Remove(clip);
                _lru.AddFirst(clip);
                return data;
            }
            var t = (Track)clip;
            if (t.IsFile)
            {
                data = _ff.DecodePcm(t.Path, AudioMixer.Rate);
                Used.Add(t.Name);
            }
            else
            {
                var mono = ChipSynth.Music(t.SynthKey, t.SynthChapter, AudioMixer.Rate);
                data = new float[mono.Length * 2];
                for (int i = 0; i < mono.Length; i++) data[i * 2] = data[i * 2 + 1] = mono[i];
            }
            _pcm[clip] = data;
            _lru.AddFirst(clip);
            while (_lru.Count > 6)
            {
                _pcm.Remove(_lru.Last.Value);
                _lru.RemoveLast();
            }
            return data;
        }
    }

    /// <summary>
    /// Сведение звука сцены: голоса режиссёра музыки (как AudioSynth в игре — та же громкость, кроссфейды
    /// и позиции) плюс звуковые эффекты событий. Пишет WAV и считает пики для отчёта.
    /// </summary>
    public sealed class AudioMixer
    {
        public const int Rate = 44100;
        private readonly DiskLibrary _lib;
        private readonly MusicDirector _music;
        private readonly float _musicVolume, _sfxVolume;
        private float[] _buf = new float[Rate * 2 * 60];
        private long _frames;       // сколько кадров звука уже сведено
        private double _carry;
        private readonly Dictionary<string, float[]> _sfx = new Dictionary<string, float[]>();
        private readonly Dictionary<string, double> _sfxLast = new Dictionary<string, double>();

        private sealed class Head
        {
            public IMusicClip Clip;
            public int Starts = -1;
            public long Pos;
            public float Vol;
        }

        private readonly List<Head> _heads = new List<Head>();
        private readonly Head _stingHead = new Head();

        public AudioMixer(DiskLibrary lib, MusicDirector music, Config cfg)
        {
            _lib = lib;
            _music = music;
            // Те же формулы громкости, что в игре: AudioSynth.SetVolumes и AudioListener.volume.
            _musicVolume = 0.44f * cfg.MusicVolume * cfg.MasterVolume;
            _sfxVolume = 0.625f * cfg.SfxVolume * cfg.MasterVolume;
        }

        public double Seconds => _frames / (double)Rate;

        private void Ensure(long frames)
        {
            long need = frames * 2;
            if (need <= _buf.Length) return;
            long n = _buf.Length;
            while (n < need) n *= 2;
            Array.Resize(ref _buf, (int)n);
        }

        /// <summary>Звуковой эффект с текущего момента (повтор того же звука чаще 45 мс глушится — как в игре).</summary>
        public void Sfx(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            double now = Seconds;
            if (_sfxLast.TryGetValue(key, out var last) && now - last < 0.045) return;
            _sfxLast[key] = now;
            if (!_sfx.TryGetValue(key, out var data)) _sfx[key] = data = ChipSynth.Sfx(key, Rate);
            Ensure(_frames + data.Length + 1);
            long o = _frames * 2;
            for (int i = 0; i < data.Length; i++)
            {
                float s = data[i] * _sfxVolume;
                _buf[o + i * 2] += s;
                _buf[o + i * 2 + 1] += s;
            }
        }

        /// <summary>Свести dt секунд музыки по текущему состоянию голосов (вызывать после MusicDirector.Update).</summary>
        public void Advance(float dt)
        {
            _carry += dt * Rate;
            int n = (int)_carry;
            _carry -= n;
            if (n <= 0) return;
            Ensure(_frames + n + 1);
            while (_heads.Count < _music.Voices.Count) _heads.Add(new Head());
            for (int i = 0; i < _music.Voices.Count; i++) MixVoice(_music.Voices[i], _heads[i], n);
            MixVoice(_music.Sting, _stingHead, n);
            _frames += n;
        }

        private void MixVoice(MusicVoice v, Head h, int n)
        {
            if (v.Clip == null || !v.Playing)
            {
                h.Clip = null;
                return;
            }
            float vol1 = v.Volume * _musicVolume;
            if (h.Clip != v.Clip || h.Starts != v.Starts)
            {
                h.Clip = v.Clip;
                h.Starts = v.Starts;
                h.Pos = (long)(v.Time * Rate);
                h.Vol = vol1;
            }
            var pcm = _lib.Pcm(v.Clip);
            long len = pcm.Length / 2;
            if (len == 0) return;
            long o = _frames * 2;
            for (int k = 0; k < n; k++)
            {
                long p = h.Pos + k;
                if (p >= len)
                {
                    if (!v.Loop) break;
                    p %= len;
                }
                float g = h.Vol + (vol1 - h.Vol) * (k + 1) / n;
                _buf[o + k * 2] += pcm[p * 2] * g;
                _buf[o + k * 2 + 1] += pcm[p * 2 + 1] * g;
            }
            h.Pos += n;
            if (v.Loop && h.Pos >= len) h.Pos %= len;
            h.Vol = vol1;
        }

        public sealed class Stats
        {
            public float Peak;
            public double ClippedPercent;
        }

        /// <summary>WAV 16 бит стерео; перегруз обрезается, как на выходе Unity, и попадает в отчёт.</summary>
        public Stats WriteWav(string path)
        {
            long frames = _frames;
            var st = new Stats();
            long clipped = 0;
            using var fs = File.Create(path);
            using var w = new BinaryWriter(fs);
            int dataBytes = (int)(frames * 4);
            w.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
            w.Write(36 + dataBytes);
            w.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E', (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
            w.Write(16);
            w.Write((short)1);
            w.Write((short)2);
            w.Write(Rate);
            w.Write(Rate * 4);
            w.Write((short)4);
            w.Write((short)16);
            w.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
            w.Write(dataBytes);
            var bytes = new byte[frames * 4];
            for (long i = 0; i < frames * 2; i++)
            {
                float s = _buf[i];
                float a = Math.Abs(s);
                if (a > st.Peak) st.Peak = a;
                if (a > 1f)
                {
                    clipped++;
                    s = s > 0 ? 1f : -1f;
                }
                short v = (short)(s * 32767f);
                bytes[i * 2] = (byte)v;
                bytes[i * 2 + 1] = (byte)(v >> 8);
            }
            w.Write(bytes);
            st.ClippedPercent = frames > 0 ? clipped * 100.0 / (frames * 2) : 0;
            return st;
        }
    }
}
