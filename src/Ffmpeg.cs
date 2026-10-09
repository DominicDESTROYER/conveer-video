using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Conveer
{
    /// <summary>Обёртка над ffmpeg/ffprobe: кодирование кадров из трубы, сведение со звуком, длительность и декодирование треков.</summary>
    public sealed class Ffmpeg
    {
        private readonly Config _cfg;
        private bool? _libass;

        public Ffmpeg(Config cfg)
        {
            _cfg = cfg;
        }

        public string Version()
        {
            try
            {
                var r = Run(_cfg.Ffmpeg, "-hide_banner -version", null, out string err);
                return r.Split('\n')[0].Trim();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Есть ли в сборке ffmpeg фильтр subtitles (libass) – чтобы вшивать русские субтитры в кадр.</summary>
        public bool HasLibass
        {
            get
            {
                if (_libass == null)
                {
                    string o = Run(_cfg.Ffmpeg, "-hide_banner -filters", null, out _);
                    _libass = o.Contains(" subtitles ");
                }
                return _libass.Value;
            }
        }

        public double Duration(string path)
        {
            string o = Run(_cfg.Ffprobe, $"-v error -show_entries format=duration -of csv=p=0 \"{path}\"", null, out _);
            return double.TryParse(o.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        }

        /// <summary>Кодек и параметры потока (для отчёта: например, MP3 внутри .ogg).</summary>
        public string Codec(string path)
        {
            string o = Run(_cfg.Ffprobe, $"-v error -select_streams a:0 -show_entries stream=codec_name,sample_rate,channels -of csv=p=0 \"{path}\"", null, out _);
            return o.Trim();
        }

        /// <summary>Интегральная громкость (LUFS) и истинный пик (dBTP) по EBU R128.</summary>
        public (double lufs, double peak) Loudness(string path)
        {
            Run(_cfg.Ffmpeg, $"-hide_banner -nostats -i \"{path}\" -filter_complex ebur128=peak=true -f null -", null, out string err);
            double lufs = double.NaN, peak = double.NaN;
            int sum = err.LastIndexOf("Summary:", StringComparison.Ordinal);
            if (sum >= 0)
            {
                string s = err.Substring(sum);
                lufs = Num(s, "I:");
                peak = Num(s, "Peak:");
            }
            return (lufs, peak);
        }

        private static double Num(string s, string label)
        {
            int i = s.IndexOf(label, StringComparison.Ordinal);
            if (i < 0) return double.NaN;
            i += label.Length;
            while (i < s.Length && s[i] == ' ') i++;
            int j = i;
            while (j < s.Length && (char.IsDigit(s[j]) || s[j] == '-' || s[j] == '.')) j++;
            return double.TryParse(s.Substring(i, j - i), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : double.NaN;
        }

        /// <summary>
        /// Громкость по окнам 0,5 с (дБ, моно 4 кГц) – чтобы найти тихое вступление и долгое затухание в конце.
        /// </summary>
        public List<double> Envelope(string path)
        {
            var psi = new ProcessStartInfo(_cfg.Ffmpeg, $"-v error -i \"{path}\" -f s16le -ac 1 -ar 4000 -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            var b = ms.ToArray();
            var env = new List<double>();
            const int w = 2000;
            for (int i = 0; i + w * 2 <= b.Length; i += w * 2)
            {
                double sum = 0;
                for (int k = 0; k < w; k++)
                {
                    double v = (short)(b[i + k * 2] | (b[i + k * 2 + 1] << 8)) / 32768.0;
                    sum += v * v;
                }
                env.Add(10 * Math.Log10(sum / w + 1e-12));
            }
            return env;
        }

        /// <summary>Трек целиком в стерео float 44.1 кГц.</summary>
        public float[] DecodePcm(string path, int rate)
        {
            var psi = new ProcessStartInfo(_cfg.Ffmpeg, $"-v error -i \"{path}\" -f f32le -ac 2 -ar {rate} -")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            using var ms = new MemoryStream();
            p.StandardOutput.BaseStream.CopyTo(ms);
            p.WaitForExit();
            var bytes = ms.ToArray();
            var data = new float[bytes.Length / 4];
            Buffer.BlockCopy(bytes, 0, data, 0, data.Length * 4);
            if (p.ExitCode != 0) Console.Error.WriteLine("  ffmpeg: не удалось декодировать " + path + ": " + errTask.Result.Trim());
            return data;
        }

        /// <summary>Видеопоток без звука: RGB-кадры в stdin, H.264 на выходе, при желании – вшитые субтитры (.ass).</summary>
        public sealed class VideoWriter : IDisposable
        {
            private readonly Process _p;
            private readonly Stream _in;
            private readonly Task<string> _err;
            public readonly string Path;

            public VideoWriter(Ffmpeg ff, string path, int w, int h, string assFile)
            {
                Path = path;
                var c = ff._cfg;
                string vf = $"scale={c.Width}:{c.Height}:flags=neighbor";
                string dir = null;
                if (assFile != null)
                {
                    // Путь к субтитрам – относительный (рабочая папка процесса): так фильтр не спотыкается о «C:\».
                    dir = System.IO.Path.GetDirectoryName(assFile);
                    vf += ",subtitles=" + System.IO.Path.GetFileName(assFile);
                }
                var psi = new ProcessStartInfo(c.Ffmpeg,
                    $"-y -v error -f rawvideo -pix_fmt rgb24 -s {w}x{h} -r {c.Fps} -i - -vf \"{vf}\" -c:v libx264 -preset {c.Preset} -crf {c.Crf} -pix_fmt yuv420p \"{path}\"")
                {
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    WorkingDirectory = dir ?? Environment.CurrentDirectory,
                };
                _p = Process.Start(psi);
                _in = _p.StandardInput.BaseStream;
                _err = _p.StandardError.ReadToEndAsync();
            }

            public void Write(Frame f) => _in.Write(f.Rgb, 0, f.Rgb.Length);

            public void Dispose()
            {
                _in.Close();
                _p.WaitForExit();
                if (_p.ExitCode != 0) throw new Exception("ffmpeg (видео): " + _err.Result.Trim());
            }
        }

        /// <summary>
        /// Видео + WAV → итоговый MP4. ass – вшить субтитры в кадр (перекодирование, нужен libass),
        /// иначе srt – отдельной дорожкой (включается в плеере).
        /// </summary>
        public void Mux(string video, string wav, string srt, string ass, string output)
        {
            var args = new StringBuilder($"-y -v error -i \"{video}\" -i \"{wav}\"");
            bool soft = srt != null && ass == null;
            if (soft) args.Append($" -i \"{srt}\"");
            args.Append(" -map 0:v -map 1:a");
            if (ass != null) args.Append($" -vf subtitles={System.IO.Path.GetFileName(ass)} -c:v libx264 -preset {_cfg.Preset} -crf {_cfg.Crf} -pix_fmt yuv420p");
            else args.Append(" -c:v copy");
            if (soft) args.Append(" -map 2:s -c:s mov_text -metadata:s:s:0 language=rus");
            args.Append($" -c:a aac -b:a 192k -movflags +faststart -shortest \"{output}\"");
            Run(_cfg.Ffmpeg, args.ToString(), ass != null ? System.IO.Path.GetDirectoryName(ass) : null, out _, true);
        }

        private static string Run(string exe, string args, string workDir, out string stderr, bool check = false)
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            if (workDir != null) psi.WorkingDirectory = workDir;
            using var p = Process.Start(psi);
            var errTask = p.StandardError.ReadToEndAsync();
            string o = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            stderr = errTask.Result;
            if (check && p.ExitCode != 0) throw new Exception(exe + ": " + stderr.Trim());
            return o;
        }
    }
}
