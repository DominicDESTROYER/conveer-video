using System;
using System.Collections.Generic;
using System.Linq;
using Tokenfall.Art;
using Tokenfall.Art.Audio;
using Tokenfall.Art.Cinema;
using Tokenfall.Core;
using Tokenfall.Core.Content;
using Tokenfall.Core.Generation;
using Tokenfall.Core.Meta;
using Tokenfall.Core.Progression;
using Tokenfall.Core.Simulation;
using UnityEngine;

namespace Conveer
{
    /// <summary>Сцена конвейера: одно видео со своим сценарием.</summary>
    public sealed class SceneDef
    {
        public string Id;
        public string Title;
        public string Description;
        public string[] Expect = new string[0];   // треки каталога, которые должны прозвучать
        public Action<Session, Studio> Shoot;
    }

    /// <summary>
    /// Съёмочная площадка: общие отрисовщики и шаги сценариев — забег с ботом, бой с боссом, ролик,
    /// экран итогов, титул, Убежище. Каждый шаг сообщает MusicFlow о том же, о чём сообщает GameApp в игре.
    /// </summary>
    public sealed class Studio
    {
        private readonly Config _cfg;
        public readonly WorldRenderer World;
        private readonly CineCanvas _canvas = new CineCanvas();
        private readonly Dictionary<string, CineAssets> _cine = new Dictionary<string, CineAssets>();
        private readonly Frame _last;

        public Studio(Config cfg)
        {
            _cfg = cfg;
            World = new WorldRenderer(Math.Max(16, (int)Math.Round(cfg.Height / 11.25f)));
            _last = new Frame(cfg.Width, cfg.Height);
        }

        private float FrameDt => 1f / _cfg.Fps;

        // ───────────── Забег ─────────────

        /// <summary>Забег на нужном этаже и биоме. Герой неуязвим, чтобы бот не погиб посреди съёмки.</summary>
        public Run NewRun(int stage, string biome, uint seed, string character = "toki")
        {
            var run = new Run(CharacterDatabase.Get(character) ?? CharacterDatabase.All[0], seed, new SaveData(), new MetaBonuses { Difficulty = Difficulty.Normal });
            if (stage > 1 || biome != null) run.StartStage(Math.Max(1, stage), biome, false);
            run.Events.Clear();
            run.ShieldTime = 1e6f;
            World.ResetCamera();
            return run;
        }

        /// <summary>События ядра → звуки и музыка (как OnEvent в GameApp). Вызывать и после шагов вне такта: вход в комнату, новый этаж.</summary>
        public void OnEvents(Session s, Run run)
        {
            foreach (var e in run.Events)
            {
                s.Sfx(GameSounds.For(e));
                switch (e.Type)
                {
                    case GameEventType.StageEnter:
                        if (!run.IsHub) s.Flow.StageEntered(run);
                        s.Note("этаж " + run.Stage + ": " + run.Biome.Key);
                        break;
                    case GameEventType.BossIntro:
                        s.Note("вход к боссу " + e.Key);
                        break;
                    case GameEventType.BossDefeated:
                        s.Note("босс повержен: " + e.Key);
                        break;
                }
            }
        }

        /// <summary>Игра ботом (bot = null — герой стоит): seconds секунд или пока stop() не скажет «хватит». extra — логика такта.</summary>
        public void Play(Session s, Run run, AutoPilot bot, float seconds, Func<bool> stop = null, Action<float> extra = null)
        {
            int n = (int)Math.Round(seconds / Session.Dt);
            float elapsed = 0f;
            for (int i = 0; i < n; i++)
            {
                s.Tick(() =>
                {
                    run.Tick(bot != null ? bot.Next(run, Session.Dt) : default(InputFrame), Session.Dt);
                    // Подмога и урон сцены — до чтения событий, чтобы их звуки и победа над боссом не потерялись.
                    extra?.Invoke(elapsed);
                    OnEvents(s, run);
                    // Как GameApp: музыка забега (босс, джингл победы, возврат фона) — после такта симуляции.
                    if (!run.IsHub && !run.Over) s.Flow.Tick(run);
                }, f => DrawRun(run, f));
                elapsed += Session.Dt;
                if (stop != null && stop()) break;
            }
        }

        private void DrawRun(Run run, Frame f)
        {
            World.Render(run, f, FrameDt);
            _last.CopyFrom(f);
        }

        /// <summary>Сцена с боссом: фон биома, вход в комнату босса, бой, джингл победы и возвращение фона.</summary>
        public void BossFight(Session s, string bossKey, int stage, string biome)
        {
            var run = NewRun(stage, biome, (uint)(Rng.Hash(bossKey) & 0x7FFFFFFF) | 1u);
            var bot = new AutoPilot(17u);
            s.Flow.RunStarted(run);
            s.Note("забег: этаж " + run.Stage + ", " + run.Biome.Key);
            Play(s, run, bot, _cfg.Sec("beforeBoss"));

            var node = run.Floor.Rooms.First(r => r.Type == RoomType.Boss);
            node.BossKey = bossKey;
            node.Revealed = true;
            run.EnterRoom(node.Index, null);
            OnEvents(s, run);
            run.Events.Clear();
            World.ResetCamera();

            float limit = _cfg.Sec("bossFight");
            Play(s, run, bot, limit + 30f, () => run.Room.Cleared, t => Assist(run, t, limit));
            if (!run.Room.Cleared) s.Note("бой не закончился за отведённое время");
            // Герой стоит в комнате босса: слышно джингл победы и возвращение фона (бот иначе сразу прыгает в люк).
            Play(s, run, null, _cfg.Sec("afterBoss"));
        }

        /// <summary>
        /// Подмога боту, чтобы бой укладывался в нужное время: босс теряет здоровье не медленнее графика
        /// (урон идёт обычным путём Run.DamageEnemy — фазы, неуязвимость между фазами и деление работают как в игре).
        /// </summary>
        private static void Assist(Run run, float elapsed, float limit)
        {
            float target = Math.Max(0f, 1f - elapsed / limit);
            foreach (var e in run.Room.Entities.ToArray())
            {
                if (!(e is EnemyEntity en) || en.Dead || en.Dummy || en.Charm > 0f) continue;
                if (en is BossEntity b)
                {
                    if (b.IsSegment) continue;
                    float extra = en.Hp - target * en.MaxHp;
                    if (extra > 0f) run.DamageEnemy(en, Math.Min(extra, en.MaxHp * 0.02f) + 0.01f, TearFlags.None, Vec2.Zero);
                }
                else if (elapsed > limit * 0.8f) run.DamageEnemy(en, Math.Max(1f, en.MaxHp * 0.04f), TearFlags.None, Vec2.Zero);
            }
        }

        /// <summary>Смерть героя: урон, пока забег не закончится.</summary>
        public void Die(Session s, Run run, AutoPilot bot)
        {
            run.ShieldTime = 0f;
            s.Note("герой получает смертельный урон");
            Play(s, run, bot, 12f, () => run.Over, _ =>
            {
                if (!run.Over) run.DamagePlayer(1, "conveer", true);
            });
        }

        /// <summary>Конец забега, как FinishRun в игре: музыка затихает, джингл итогов (если дальше не ролик).</summary>
        public void Finish(Session s, Run run, bool won, bool cinemaFollows)
        {
            run.Won = won;
            bool sting = s.Flow.RunFinished(run, cinemaFollows);
            if (!sting && !cinemaFollows) s.Sfx(won ? "revive" : "descend");
            s.Note(won ? "победа в забеге" : "смерть — экран итогов");
        }

        /// <summary>Экран итогов: затемнённый последний кадр, надпись; затем игрок уходит в Убежище.</summary>
        public void Results(Session s, bool won, float seconds)
        {
            var bg = new Frame(_cfg.Width, _cfg.Height);
            bg.CopyFrom(_last);
            bg.Dim(0.3f);
            float t0 = s.T;
            s.Wait(seconds, null, f =>
            {
                f.CopyFrom(bg);
                int sc = Math.Max(2, f.H / 72);
                string title = won ? "VICTORY" : "GAME OVER";
                var c = won ? new Color(0.5f, 1f, 0.6f) : new Color(1f, 0.35f, 0.4f);
                f.Text(title, (f.W - Frame.TextWidth(title, sc)) / 2, f.H / 3, sc, c);
                int left = (int)Math.Ceiling(seconds - (s.T - t0));
                string hint = "ENTER - TO HUB  (" + left + ")";
                int hs = Math.Max(1, sc / 3);
                f.Text(hint, (f.W - Frame.TextWidth(hint, hs)) / 2, f.H / 3 + sc * 9, hs, new Color(0.8f, 0.85f, 0.95f));
            });
        }

        /// <summary>Игрок нажал «В Убежище» на экране итогов: джингл тут же уходит, звучит тема базы.</summary>
        public void LeaveToHub(Session s, float seconds)
        {
            s.Note("уход с экрана итогов в Убежище");
            s.Flow.LeftResults();
            s.Flow.Hub();
            Hub(s, seconds, false);
        }

        public void Hub(Session s, float seconds, bool startMusic = true)
        {
            var save = new SaveData();
            foreach (var c in CharacterDatabase.All.Take(4)) if (c.Unlock != null) save.Unlocks.Add(c.Unlock.Key);
            var hub = Run.CreateHub(save, CharacterDatabase.Get("toki"), 1000);
            hub.Events.Clear();
            World.ResetCamera();
            if (startMusic) s.Flow.Hub();
            Play(s, hub, new AutoPilot(5u), seconds);
        }

        /// <summary>Титул: демо-забег бота за затемнением, логотип и меню.</summary>
        public void Title(Session s, float seconds)
        {
            s.Flow.Title();
            uint seed = 0x5EED0001;
            var run = new Run(CharacterDatabase.All[(int)(seed % 3)], seed, new SaveData());
            run.Events.Clear();
            var bot = new AutoPilot(seed);
            World.ResetCamera();
            int n = (int)Math.Round(seconds / Session.Dt);
            for (int i = 0; i < n; i++)
            {
                s.Tick(() =>
                {
                    run.Tick(bot.Next(run, Session.Dt), Session.Dt);
                    if (run.Over)
                    {
                        seed++;
                        run = new Run(CharacterDatabase.All[(int)(seed % 3)], seed, new SaveData());
                        run.Events.Clear();
                        bot = new AutoPilot(seed);
                        World.ResetCamera();
                    }
                    // Демо беззвучно (как в игре): только тема титула.
                    run.Events.Clear();
                }, f =>
                {
                    World.Render(run, f, FrameDt);
                    f.Dim(0.45f);
                    int sc = Math.Max(3, f.H / 48);
                    f.Text("TOKENFALL", (f.W - Frame.TextWidth("TOKENFALL", sc)) / 2, f.H / 5, sc, new Color(0.35f, 0.85f, 1f));
                    int ms = Math.Max(1, sc / 4);
                    f.Text("OUT OF MEMORY", (f.W - Frame.TextWidth("OUT OF MEMORY", ms * 2)) / 2, f.H / 5 + sc * 7, ms * 2, new Color(1f, 0.6f, 0.75f));
                    string[] menu = { "PLAY", "SEED", "ENCYCLOPEDIA", "GALLERY", "SETTINGS", "HELP", "QUIT" };
                    for (int m = 0; m < menu.Length; m++)
                        f.Text(menu[m], (f.W - Frame.TextWidth(menu[m], ms * 2)) / 2, f.H / 2 + m * ms * 14, ms * 2, m == 0 ? new Color(1f, 0.85f, 0.3f) : new Color(0.85f, 0.9f, 1f));
                    _last.CopyFrom(f);
                });
            }
        }

        // ───────────── Ролики ─────────────

        /// <summary>Ролик: та же музыка (MusicFlow.Cinema), звуковые метки и субтитры, кадр 320×180 с целым увеличением.</summary>
        public void Cinema(Session s, string id)
        {
            var scene = CineCatalog.Get(id);
            if (!_cine.TryGetValue(id, out var assets)) _cine[id] = assets = CineAssets.Build(scene.Needs);
            s.Note("ролик " + id + " (" + scene.Duration.ToString("0.0") + " с)");
            s.Flow.Cinema(scene.Music, scene.Chapter);
            s.AddSubtitles(scene, s.T);
            float t = 0f;
            int cue = 0;
            while (t < scene.Duration)
            {
                s.Tick(() =>
                {
                    t += Session.Dt;
                    while (cue < scene.Cues.Count && scene.Cues[cue].T <= t) s.Sfx(scene.Cues[cue++].Sfx);
                }, f =>
                {
                    scene.Render(_canvas, assets, t);
                    f.Clear(0.055f, 0.047f, 0.11f);
                    int k = Math.Max(1, Math.Min(f.W / CineCanvas.W, f.H / CineCanvas.H));
                    f.Blit(_canvas.Img, (f.W - CineCanvas.W * k) / 2, (f.H - CineCanvas.H * k) / 2, k);
                    _last.CopyFrom(f);
                });
            }
        }

        // ───────────── Каталог сцен ─────────────

        private static (string boss, string title)[] TierBosses =
        {
            ("sin_ragequit", "Мини-босс «Rage-quit»"),
            ("great_captcha", "Уровень I · Великая Капча"),
            ("quantizer", "Уровень II · Квантизатор"),
            ("colossus", "Уровень III · Колосс"),
            ("alignment_angel", "Уровень IV · Ангел Выравнивания"),
            ("delirium", "Уровень V · Шумовой хор"),
            ("oom_killer", "Финал · OOM Killer"),
            ("dead_internet_beast", "Секрет · Хозяин Мёртвого Интернета"),
        };

        /// <summary>Биом и этаж, где игрок встречает босса.</summary>
        private static (int stage, string biome) WhereIs(BossDef b)
        {
            var bi = BiomeDatabase.Biomes.FirstOrDefault(x => x.FinalBoss == b.Key) ?? BiomeDatabase.Biomes.FirstOrDefault(x => x.Bosses.Contains(b.Key));
            int stage = Math.Max(1, Math.Min(b.MinStage, BiomeDatabase.MaxStage));
            if (bi == null)
            {
                var ch = BiomeDatabase.ChapterForStage(stage);
                bi = BiomeDatabase.Get(ch.MainBiomes[0]);
            }
            var chapter = BiomeDatabase.Chapters.First(c => c.Index == bi.Chapter);
            stage = Math.Max(chapter.FirstStage, Math.Min(chapter.FirstStage + chapter.Floors - 1, stage));
            return (stage, bi.Key);
        }

        private static SceneDef BossScene(string id, BossDef b, string title)
        {
            var (stage, biome) = WhereIs(b);
            var slot = MusicCatalog.BossSlot(b);
            var biomeSlot = MusicCatalog.Slot(BiomeDatabase.Get(biome).Music, 1);
            return new SceneDef
            {
                Id = id,
                Title = title,
                Description = $"Этаж {stage} ({biome}): фон биома → вход в комнату босса «{b.Key}» → бой → джингл победы → фон продолжается с того же места.",
                Expect = new[] { biomeSlot, slot, "sting_boss" },
                Shoot = (s, st) => st.BossFight(s, b.Key, stage, biome),
            };
        }

        public static List<SceneDef> All(Config cfg)
        {
            var list = new List<SceneDef>();
            float afterResults = cfg.Sec("afterResults"), results = cfg.Sec("results");

            list.Add(new SceneDef
            {
                Id = "title", Title = "Титульный экран", Expect = new[] { "title" },
                Description = "Главное меню с демо-забегом на фоне: тема титула входит из тишины.",
                Shoot = (s, st) => st.Title(s, cfg.Sec("title")),
            });
            list.Add(new SceneDef
            {
                Id = "intro", Title = "Пролог «Out of Memory»", Expect = new[] { "cine_intro", "title" },
                Description = "Ролик при запуске игры, затем переход к титулу (кроссфейд cine_intro → title).",
                Shoot = (s, st) =>
                {
                    s.Flow.Title();
                    st.Cinema(s, CineCatalog.IntroId);
                    st.Title(s, 8f);
                },
            });
            list.Add(new SceneDef
            {
                Id = "hub", Title = "Убежище → новый забег", Expect = new[] { "hub", "bg_cache" },
                Description = "Тема Убежища, затем портал: забег начинается, тема Кэша сменяет базу кроссфейдом.",
                Shoot = (s, st) =>
                {
                    st.Hub(s, cfg.Sec("hub"));
                    var run = st.NewRun(1, null, 101u);
                    s.Flow.LeftResults();
                    s.Flow.RunStarted(run);
                    s.Note("портал: новый забег");
                    st.Play(s, run, new AutoPilot(3u), 10f);
                },
            });

            // Фон: по сцене на каждый трек фона (первый биом, который его использует).
            foreach (var t in MusicCatalog.Tracks.Where(x => x.Group == MusicCatalog.Group.Background))
            {
                var bi = BiomeDatabase.Biomes.FirstOrDefault(b => MusicCatalog.Slot(b.Music, b.Chapter) == t.File);
                if (bi == null) continue;
                var ch = BiomeDatabase.Chapters.First(c => c.Index == bi.Chapter);
                list.Add(new SceneDef
                {
                    Id = t.File, Title = "Фон · " + t.File, Expect = new[] { t.File },
                    Description = $"Глава {bi.Chapter}, биом {bi.Key} (ключ музыки {bi.Music}): игра ботом, тема биома.",
                    Shoot = (s, st) =>
                    {
                        var run = st.NewRun(ch.FirstStage, bi.Key, (uint)(Rng.Hash(bi.Key) & 0x7FFFFFFF) | 1u);
                        s.Flow.RunStarted(run);
                        st.Play(s, run, new AutoPilot(9u), cfg.Sec("biome"));
                    },
                });
            }
            // Личные треки биомов (файл с ключом музыки биома), если они есть.
            foreach (var bi in BiomeDatabase.Biomes)
            {
                if (DiskLibrary.FindFile(cfg.MusicDir, bi.Music) == null) continue;
                var ch = BiomeDatabase.Chapters.First(c => c.Index == bi.Chapter);
                list.Add(new SceneDef
                {
                    Id = "biome_" + bi.Key, Title = "Личный трек биома · " + bi.Music, Expect = new[] { bi.Music },
                    Description = $"Биом {bi.Key}: свой файл {bi.Music} вместо общего трека главы.",
                    Shoot = (s, st) =>
                    {
                        var run = st.NewRun(ch.FirstStage, bi.Key, 77u);
                        s.Flow.RunStarted(run);
                        st.Play(s, run, new AutoPilot(9u), cfg.Sec("biome"));
                    },
                });
            }

            // Боссы: по сцене на уровень сложности, плюс личные треки боссов.
            var shown = new HashSet<string>();
            foreach (var (key, title) in TierBosses)
            {
                var b = BossDatabase.Get(key);
                if (b == null) continue;
                shown.Add(key);
                list.Add(BossScene(MusicCatalog.BossSlot(b), b, title));
            }
            foreach (var b in BossDatabase.All)
            {
                if (shown.Contains(b.Key) || BossDatabase.IsSplitChild(b)) continue;
                if (DiskLibrary.FindFile(cfg.MusicDir, "boss_" + b.Key) == null) continue;
                list.Add(BossScene("boss_" + b.Key, b, "Личный трек босса · " + b.Key));
            }

            list.Add(new SceneDef
            {
                Id = "death", Title = "Смерть → экран итогов → Убежище", Expect = new[] { "bg_cache", "sting_defeat", "hub" },
                Description = "Герой погибает: музыка затихает, звучит джингл смерти; игрок уходит в Убежище — джингл сразу затихает, вступает тема базы.",
                Shoot = (s, st) =>
                {
                    var run = st.NewRun(1, null, 202u);
                    var bot = new AutoPilot(4u);
                    s.Flow.RunStarted(run);
                    st.Play(s, run, bot, 8f);
                    st.Die(s, run, bot);
                    st.Finish(s, run, false, false);
                    st.Results(s, false, results);
                    st.LeaveToHub(s, afterResults);
                },
            });
            list.Add(new SceneDef
            {
                Id = "victory", Title = "Победа → экран итогов → Убежище", Expect = new[] { "bg_kernel", "sting_victory", "hub" },
                Description = "Победа без нового ролика: джингл победы на экране итогов, затем уход в Убежище.",
                Shoot = (s, st) =>
                {
                    var run = st.NewRun(13, "kernel", 303u);
                    s.Flow.RunStarted(run);
                    st.Play(s, run, new AutoPilot(6u), 8f);
                    st.Finish(s, run, true, false);
                    st.Results(s, true, results);
                    st.LeaveToHub(s, afterResults);
                },
            });

            // Ролики клеток и концовка: как после победы в игре — ролик, итоги с джинглом, Убежище.
            for (int n = 1; n <= MemoryCells.Max; n++)
            {
                int cell = n;
                string slot = MusicCatalog.CellSlot(cell);
                list.Add(new SceneDef
                {
                    Id = "cell_" + cell.ToString("00"), Title = $"Клетка памяти {cell} · «{Loc.T(MemoryCells.Get(cell).Name)}»",
                    Expect = new[] { slot, "sting_victory", "hub" },
                    Description = $"Победа открыла клетку {cell}: ролик (трек {slot} или cine_cell_{cell}), экран итогов с джинглом победы, уход в Убежище.",
                    Shoot = (s, st) => CinemaAfterWin(s, st, CineCatalog.CellId(cell), results, afterResults),
                });
            }
            list.Add(new SceneDef
            {
                Id = "ending", Title = "Концовка «Hello, world.»", Expect = new[] { "cine_ending", "sting_victory", "hub" },
                Description = "Победа со всеми клетками: ролик концовки, экран итогов, уход в Убежище.",
                Shoot = (s, st) => CinemaAfterWin(s, st, CineCatalog.EndingId, results, afterResults),
            });
            list.Add(new SceneDef
            {
                Id = "gallery", Title = "Галерея роликов → титул", Expect = new[] { "title", "cine_cell_a" },
                Description = "Ролик из галереи меню: после него снова звучит тема титула (раньше в галерее оставалась тишина).",
                Shoot = (s, st) =>
                {
                    st.Title(s, 6f);
                    s.Note("галерея: ролик клетки 1");
                    st.Cinema(s, CineCatalog.CellId(1));
                    s.Flow.Title();
                    st.Title(s, 8f);
                },
            });
            list.Add(new SceneDef
            {
                Id = "transitions", Title = "Смена глав: все переходы фона",
                Expect = BiomeDatabase.Chapters.Select(c => MusicCatalog.Slot(BiomeDatabase.Get(c.MainBiomes[0]).Music, c.Index)).Distinct().ToArray(),
                Description = "Забег проходит главы 1 → 10 подряд: на каждой смене этажа тема биома сменяется кроссфейдом.",
                Shoot = (s, st) =>
                {
                    var run = st.NewRun(1, null, 404u);
                    var bot = new AutoPilot(8u);
                    s.Flow.RunStarted(run);
                    float seg = cfg.Sec("transition");
                    st.Play(s, run, bot, seg);
                    foreach (var ch in BiomeDatabase.Chapters.Where(c => c.Index > 1))
                    {
                        run.ShieldTime = 1e6f;
                        run.StartStage(ch.FirstStage, ch.MainBiomes[0], false);
                        st.OnEvents(s, run);
                        run.Events.Clear();
                        st.World.ResetCamera();
                        st.Play(s, run, bot, seg);
                    }
                },
            });
            return list;
        }

        private static void CinemaAfterWin(Session s, Studio st, string cineId, float results, float afterResults)
        {
            var run = st.NewRun(13, "kernel", 505u);
            s.Flow.RunStarted(run);
            st.Play(s, run, new AutoPilot(2u), 5f);
            st.Finish(s, run, true, true);
            st.Cinema(s, cineId);
            s.Flow.CinemaToResults();
            s.Note("экран итогов после ролика");
            st.Results(s, true, results);
            st.LeaveToHub(s, afterResults);
        }
    }
}
