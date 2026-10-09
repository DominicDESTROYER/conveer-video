using System;
using System.Collections.Generic;
using System.Linq;
using Tokenfall.Art;
using Tokenfall.Core;
using Tokenfall.Core.Content;
using Tokenfall.Core.Generation;
using Tokenfall.Core.Simulation;
using Tokenfall.Game;
using UnityEngine;

namespace Conveer
{
    /// <summary>
    /// Кадр забега без Unity: плитки и предметы – процедурные спрайты игры (SpriteFactory), герои, враги и боссы –
    /// листы пиксельной студии, как в WorldView. Статичный слой комнаты (пол, стены, двери, препятствия)
    /// рисуется один раз и только перерисовывается при изменениях; камера плавно следует за героем.
    /// </summary>
    public sealed class WorldRenderer
    {
        private readonly SpriteFactory _f = new SpriteFactory();
        private readonly Dictionary<string, Sprite> _art = new Dictionary<string, Sprite>();
        private readonly int _base;
        /// <summary>Пикселей на клетку в текущем кадре: на арене многочастного босса камера отъезжает, чтобы влезла вся комната.</summary>
        private int _u;
        private Frame _static;
        private RoomRuntime _staticRoom;
        private long _staticHash;
        private float _camX = float.NaN, _camY;
        private Run _camRun;

        public WorldRenderer(int unit)
        {
            _base = _u = unit;
        }

        private static Color ToColor(uint argb) => new Color(((argb >> 16) & 0xFF) / 255f, ((argb >> 8) & 0xFF) / 255f, (argb & 0xFF) / 255f, ((argb >> 24) & 0xFF) / 255f);
        private static Color Lerp(Color a, Color b, float t) => new Color(a.r + (b.r - a.r) * t, a.g + (b.g - a.g) * t, a.b + (b.b - a.b) * t, a.a + (b.a - a.a) * t);
        private static Color Boost(Color c) => new Color(Math.Min(1f, c.r * 1.45f + 0.05f), Math.Min(1f, c.g * 1.45f + 0.05f), Math.Min(1f, c.b * 1.45f + 0.05f), 1f);

        /// <summary>Сбросить камеру (новая сцена или новая комната – без «проезда» через полкарты).</summary>
        public void ResetCamera() => _camX = float.NaN;

        public void Render(Run run, Frame img, float dt)
        {
            var room = run.Room;
            _u = ArenaBody(run) != null ? Math.Min(_base, (int)(img.H / (room.H + 3.2f))) : _base;
            float U = _u;
            float vw = img.W / U, vh = img.H / U;
            // Камера: комната, которая помещается в кадр, – по центру; большая – за героем, но не за стенами.
            float tx = room.W + 2 <= vw ? (room.W - vw) / 2f : Math.Max(-1f, Math.Min(run.Player.Pos.X - vw / 2f, room.W + 1 - vw));
            float ty = room.H + 2 <= vh ? (room.H - vh) / 2f : Math.Max(-1f, Math.Min(run.Player.Pos.Y - vh / 2f, room.H + 1 - vh));
            // Арена финала: как в игре (CameraRig.ArenaCenterRow) – в кадре и тело, залезающее на верхнюю стену, и весь пол.
            if (ArenaBody(run) != null) ty = 6.4f - vh / 2f;
            if (float.IsNaN(_camX) || _camRun != run || _staticRoom != room)
            {
                _camX = tx;
                _camY = ty;
                _camRun = run;
            }
            else
            {
                float k = 1f - (float)Math.Exp(-8f * dt);
                _camX += (tx - _camX) * k;
                _camY += (ty - _camY) * k;
            }
            float camX = _camX, camY = _camY;
            float PX(float x) => (x - camX) * U;
            float PY(float y) => (y - camY) * U;

            DrawStatic(run, img, camX, camY);

            var accent = ToColor(run.Biome.AccentColor);
            var draws = new List<(float order, Action act)>();
            foreach (var e in run.AllEntities())
            {
                if (!e.Visible) continue;
                var en = e;
                string sprite = e.Sprite;
                Color col = ToColor(e.Tint);
                float scale = e.Scale, height = e.Height, ord = e.Pos.Y, angle = 0f;
                bool flip = (e.Type == EntityType.Player || e.Type == EntityType.Enemy) && e.Facing.X < -0.1f;
                switch (e.Type)
                {
                    case EntityType.Player: sprite = "player:" + run.Character.Key; col = Color.white; scale = 1f; height = 0f; break;
                    case EntityType.Zone:
                        DrawZone(run, (Zone)e, img, draws, camX, camY);
                        continue;
                    case EntityType.Projectile:
                    {
                        // Как WorldView: пуля летит носом вперёд; звёздочки, крестики, квадраты и шестигранники крутятся.
                        var pr = (Projectile)e;
                        scale = pr.Radius * 2f / 0.9f;
                        bool spin = sprite == "tear" || (!pr.FromPlayer && (pr.Shape == ShotShape.Star || pr.Shape == ShotShape.Cross || pr.Shape == ShotShape.Square || pr.Shape == ShotShape.Hex));
                        angle = (float)Math.Atan2(pr.Vel.Y, pr.Vel.X) + (spin ? (run.Time * (sprite == "tear" ? 600f : 260f) + pr.Id * 37f) * (float)Math.PI / 180f : 0f);
                        if (!pr.FromPlayer && pr.Spark) col = Lerp(col, Color.white, 0.6f);
                        if (pr.FromPlayer && sprite == "tear" && (pr.Flags & TearFlags.Explosive) != 0) { sprite = "tear_bomb"; col = Color.white; }
                        break;
                    }
                    case EntityType.Pickup:
                        var pk = (Pickup)e;
                        if (pk.Kind == PickupKind.Patch) sprite = "patch:" + pk.Value;
                        if (pk.Kind == PickupKind.Trinket) sprite = "item:" + (ItemDatabase.Get(pk.Value)?.Key ?? "adblock");
                        col = Color.white;
                        break;
                    case EntityType.Pedestal:
                    {
                        var ped = (Pedestal)e;
                        col = Color.white;
                        if (ped.Item != null)
                        {
                            string ik = "item:" + ped.Item.Key;
                            draws.Add((ord + 0.01f, () => img.Draw(_f.Get(ik), PX(en.Pos.X), PY(en.Pos.Y - 0.75f), U, 1, 1, 0, Color.white)));
                        }
                        height = 0f;
                        break;
                    }
                    case EntityType.Beam:
                    {
                        var b = (Beam)e;
                        float ang = (float)Math.Atan2(b.Dir.Y, b.Dir.X);
                        Color bc = col;
                        bc.a = b.Warmup > 0 ? 0.5f : 0.95f;
                        float w = b.Warmup > 0 ? 0.12f : b.Width;
                        draws.Add((900, () => img.Draw(_f.Get("beam"), PX(b.Pos.X), PY(b.Pos.Y), U, b.Length, w, ang, bc)));
                        continue;
                    }
                    case EntityType.Effect:
                    {
                        var fx = (Effect)e;
                        col.a *= fx.Alpha;
                        if (fx.Sprite == "zap")
                        {
                            Vec2 d = fx.EndPos - fx.Pos;
                            float za = (float)Math.Atan2(d.Y, d.X), zl = d.Length;
                            Color zc = col;
                            draws.Add((910, () => img.Draw(_f.Get("zap"), PX(fx.Pos.X), PY(fx.Pos.Y), U, zl, 0.35f, za, zc)));
                            continue;
                        }
                        // Как WorldView: дуга удара распахивается, ударные волны и вспышки растут, угли и лужи лежат на полу.
                        if (fx.Sprite == "swing") angle = fx.Angle + (-35f + 70f * (1f - fx.Alpha)) * (float)Math.PI / 180f;
                        if (fx.Sprite == "poof" || fx.Sprite == "shockwave" || fx.Sprite == "splash" || fx.Sprite == "explosion" || fx.Sprite == "crate_open")
                            scale = fx.Scale * (1.25f - 0.55f * fx.Alpha);
                        if (fx.Sprite == "spark") angle = fx.Age * 4f * (float)Math.PI;
                        if (fx.Sprite.StartsWith("scar_"))
                        {
                            // Следы ударов (трещины, гарь, разрезы) лежат на полу и медленно тают.
                            angle = fx.Angle;
                            col.a = Math.Min(1f, fx.Alpha * 2f);
                            ord = -74;
                            break;
                        }
                        if (fx.HurtRadius > 0f)
                        {
                            float pulse = fx.Sprite == "hazard_embers" ? 0.75f + 0.25f * (float)Math.Sin(run.Time * 14f + fx.Id) : 0.85f + 0.15f * (float)Math.Sin(run.Time * 3f + fx.Id);
                            col.a = Math.Min(1f, fx.Life / 0.5f) * pulse;
                            ord = -75;
                            break;
                        }
                        ord = 800;
                        break;
                    }
                    case EntityType.Trapdoor: col = Color.white; ord = -80; break;
                    case EntityType.Familiar: col = Color.white; height = 0.3f; break;
                    case EntityType.Boss:
                        // Тело многочастного босса – позади своих частей (глаза висят выше его середины).
                        if (((BossEntity)e).Parts.Count > 0) ord = -4f;
                        break;
                    case EntityType.Machine:
                        col = Color.white;
                        if (((Machine)e).Kind == MachineKind.Decor && ((Machine)e).Flat) ord = -70;
                        break;
                }
                bool decor = e.Type == EntityType.Machine && ((Machine)e).Kind == MachineKind.Decor;
                if (e.Type == EntityType.Enemy && ((EnemyEntity)e).Elite)
                {
                    var el = (EnemyEntity)e;
                    var aura = new Color(0.68f, 0.5f, 1f, 0.5f);
                    float es = el.Scale;
                    draws.Add((ord - 0.003f, () => img.Draw(_f.Get("glow"), PX(en.Pos.X), PY(en.Pos.Y - en.Height * 0.6f), U, es * 1.9f, es * 1.9f, 0, aura)));
                    draws.Add((ord + 0.03f, () => img.Draw(_f.Get("elite_crown"), PX(en.Pos.X), PY(en.Pos.Y - en.Height * 0.6f - es * 0.62f), U, 0.8f, 0.8f, 0, Color.white)));
                }
                if (e.Type == EntityType.Pedestal && ((Pedestal)e).Weapon != null)
                {
                    var wd = ((Pedestal)e).Weapon;
                    var rc = ToColor(WeaponDatabase.RarityColors[Math.Min(5, wd.Rarity)]);
                    rc.a = 0.55f;
                    draws.Add((ord - 0.002f, () => img.Draw(_f.Get("glow"), PX(en.Pos.X), PY(en.Pos.Y), U, 1.4f, 1.4f, 0, rc)));
                    var rc2 = rc;
                    rc2.a = 1f;
                    draws.Add((ord + 0.01f, () => img.Draw(_f.Get("weapon:" + wd.Key), PX(en.Pos.X), PY(en.Pos.Y - 0.1f), U, 1, 1, -0.3f, rc2)));
                    continue;
                }
                if (e.Type == EntityType.Projectile || e.Type == EntityType.Pickup || (e.Type == EntityType.Machine && !decor))
                {
                    Color gc = e.Type == EntityType.Projectile ? col : new Color(0.3f, 0.85f, 1f, 1f);
                    gc.a = e.Type == EntityType.Projectile ? 0.45f : 0.25f;
                    float gs = e.Type == EntityType.Projectile ? scale * 1.6f : 1.5f;
                    draws.Add((ord - 0.002f, () => img.Draw(_f.Get("glow"), PX(en.Pos.X), PY(en.Pos.Y - en.Height * 0.6f), U, gs, gs, 0, gc)));
                }
                if (e.Type == EntityType.Machine && ((Machine)e).Kind == MachineKind.Pod)
                {
                    var pc = CharacterDatabase.Get(((Machine)e).Key);
                    bool open = pc != null && (pc.Unlock == null || run.Save.IsUnlocked(pc.Unlock.Key));
                    if (pc != null) draws.Add((ord + 0.01f, () => img.Draw(_f.Get("player:" + pc.Key), PX(en.Pos.X), PY(en.Pos.Y - 0.25f), U, 0.7f, 0.7f, 0, open ? Color.white : new Color(0.05f, 0.08f, 0.15f, 0.9f))));
                }
                if (e.Type == EntityType.Player && run.CurrentWeapon != null)
                {
                    float aim = run.Player.AimAngle;
                    bool left = Math.Cos(aim) < 0;
                    string wk = "weapon:" + run.CurrentWeapon.Key;
                    var wc = ToColor(WeaponDatabase.RarityColors[Math.Min(5, run.CurrentWeapon.Rarity)]);
                    float wx = en.Pos.X + (float)Math.Cos(aim) * 0.42f, wy = en.Pos.Y + (float)Math.Sin(aim) * 0.42f - 0.15f;
                    draws.Add((ord + 0.02f, () => img.Draw(_f.Get(wk), PX(wx), PY(wy), U, 0.8f, 0.8f, left ? aim - (float)Math.PI : aim, wc, left)));
                    var ac = ToColor(run.Character.AccentColor);
                    ac.a = 0.35f;
                    draws.Add((ord - 0.002f, () => img.Draw(_f.Get("glow"), PX(en.Pos.X), PY(en.Pos.Y), U, 1.6f, 1.6f, 0, ac)));
                }
                if (e is BossEntity mb && mb.ShowMark)
                {
                    // Метка прыжка/удара курсора: красная, когда зафиксирована.
                    var mc = mb.MarkLocked ? new Color(1f, 0.2f, 0.25f, 0.8f) : new Color(1f, 0.85f, 0.3f, 0.6f);
                    float mr = mb.MarkRadius * 2f;
                    draws.Add((-60f, () => img.Draw(_f.Get("marker"), PX(mb.MarkPos.X), PY(mb.MarkPos.Y), U, mr, mr, 0, mc)));
                }
                if (e.Flash > 0f) col = Lerp(col, Color.white, 0.75f);
                col.a *= e.Alpha;
                var studio = StudioSprite(run, e);
                if (studio != null)
                {
                    col = e.Flash > 0f ? new Color(1, 1, 1, col.a) : new Color(1, 1, 1, e.Alpha);
                    if (e.Type != EntityType.Player) flip = (Math.Abs(e.Facing.X) < 0.1f || e.Type == EntityType.Boss ? run.Player.Pos.X - e.Pos.X : e.Facing.X) < 0f;
                }
                string sp = sprite;
                Color cl = col;
                float sc = scale, hh = height, an = angle;
                bool fl = flip;
                if (e is BossEntity pe && (pe.Part != null || pe.Parts.Count > 0))
                {
                    // Части не разворачиваются к герою: правые – отражение левых; спящие – под голубым щитом.
                    fl = pe.Part != null && pe.Part.Mirror;
                    if (pe.Part != null && !Run.PartOpen(pe) && pe.Spawned <= 0f)
                    {
                        cl = Lerp(cl, new Color(0.45f, 0.75f, 1f, cl.a), 0.35f);
                        var sh = new Color(0.55f, 0.9f, 1f, 0.3f);
                        float gs = sc * 1.4f;
                        draws.Add((ord - 0.002f, () => img.Draw(_f.Get("glow"), PX(en.Pos.X), PY(en.Pos.Y - hh * 0.6f), U, gs, gs, 0, sh)));
                    }
                }
                bool noShadow = e is BossEntity ns && (ns.Parts.Count > 0 || (ns.Part != null && !ns.Part.Roams));
                if (e.Type != EntityType.Effect && e.Type != EntityType.Trapdoor && e.Type != EntityType.Projectile && !decor && !noShadow)
                    draws.Add((ord - 0.001f, () => img.Draw(_f.Get("shadow"), PX(en.Pos.X), PY(en.Pos.Y + 0.3f * sc), U, sc * 0.8f, sc * 0.8f, 0, new Color(1, 1, 1, 0.9f))));
                var spr = studio ?? _f.Get(sp);
                draws.Add((ord, () => img.Draw(spr, PX(en.Pos.X), PY(en.Pos.Y - hh * 0.6f), U, sc, sc, an, cl, fl)));
            }
            foreach (var d in draws.OrderBy(d => d.order)) d.act();
            DrawDarkness(run, img, camX, camY);
            DrawHud(run, img);
        }

        // ───────────── Зоны атак и темнота ─────────────

        private const float Rad2Deg = 180f / (float)Math.PI;

        /// <summary>
        /// Зона атаки босса, как WorldView.DrawZone: пока предупреждает – мигает всё чаще и плотнее,
        /// в момент удара вспыхивает белым, затем держится, пока активна. Лежит на полу под всеми.
        /// </summary>
        private void DrawZone(Run run, Zone z, Frame img, List<(float order, Action act)> draws, float camX, float camY)
        {
            float U = _u;
            float PX(float x) => (x - camX) * U;
            float PY(float y) => (y - camY) * U;
            Color c = ToColor(z.Tint);
            // Как WorldView: сама зона – лишь тихая подложка; предупреждение и удар рисует StrikeFx.
            if (!z.Struck) c.a = 0.1f + 0.1f * z.WarnProgress;
            else
            {
                float since = z.ActiveMax - z.Active;
                c = Lerp(c, Color.white, since < 0.06f ? 0.5f : 0.1f);
                c.a = since < 0.06f ? 0.6f : 0.28f;
            }
            float ord = z.Struck ? -64f : -65f;
            AddStrikeFx(run, z, img, draws, PX, PY);
            // Большие кольца и секторы выходят за стены – рисуем их попиксельно в пределах комнаты (как WorldView).
            bool big = (z.Shape == ZoneShape.Sector || z.Shape == ZoneShape.Ring) &&
                       (z.Pos.X - z.R < 0f || z.Pos.Y - z.R < 0f || z.Pos.X + z.R > run.Room.W || z.Pos.Y + z.R > run.Room.H);
            if (big)
            {
                draws.Add((ord, () =>
                {
                    var room = run.Room;
                    int x0 = Math.Max(0, (int)PX(0f)), x1 = Math.Min(img.W, (int)PX(room.W));
                    int y0 = Math.Max(0, (int)PY(0f)), y1 = Math.Min(img.H, (int)PY(room.H));
                    float e = 2f / U;
                    for (int y = y0; y < y1; y += 2)
                        for (int x = x0; x < x1; x += 2)
                        {
                            var p = new Vec2(camX + (x + 1f) / U, camY + (y + 1f) / U);
                            if (!z.Contains(p)) continue;
                            bool rim = !z.Contains(p + new Vec2(e, 0f)) || !z.Contains(p - new Vec2(e, 0f)) || !z.Contains(p + new Vec2(0f, e)) || !z.Contains(p - new Vec2(0f, e));
                            img.Fill(x, y, 2, 2, c.r, c.g, c.b, c.a * (rim ? 1f : 120f / 255f));
                        }
                }));
                return;
            }
            switch (z.Shape)
            {
                case ZoneShape.Circle:
                    draws.Add((ord, () => img.Draw(_f.Get("zone_disc"), PX(z.Pos.X), PY(z.Pos.Y), U, z.R, z.R, 0f, c)));
                    break;
                case ZoneShape.Ring:
                {
                    string key = SpriteFactory.ZoneRingKey(z.R > 0.01f ? z.R2 / z.R : 0f, z.GapHalf * 2f * Rad2Deg);
                    draws.Add((ord, () => img.Draw(_f.Get(key), PX(z.Pos.X), PY(z.Pos.Y), U, z.R, z.R, z.GapAt, c)));
                    break;
                }
                case ZoneShape.Sector:
                {
                    string key = SpriteFactory.ZoneSectorKey(z.Arc * 2f * Rad2Deg);
                    draws.Add((ord, () => img.Draw(_f.Get(key), PX(z.Pos.X), PY(z.Pos.Y), U, z.R, z.R, z.Angle, c)));
                    break;
                }
                case ZoneShape.Line:
                {
                    Vec2 d = z.End - z.Pos, mid = (z.Pos + z.End) * 0.5f;
                    float len = Math.Max(0.1f, d.Length), ang = (float)Math.Atan2(d.Y, d.X);
                    draws.Add((ord, () => img.Draw(_f.Get("zone_bar"), PX(mid.X), PY(mid.Y), U, len, z.Width, ang, c)));
                    break;
                }
                case ZoneShape.Rect:
                {
                    Vec2 mid = (z.Pos + z.End) * 0.5f;
                    float w = Math.Max(0.1f, z.End.X - z.Pos.X), h = Math.Max(0.1f, z.End.Y - z.Pos.Y);
                    draws.Add((ord, () => img.Draw(_f.Get("zone_bar"), PX(mid.X), PY(mid.Y), U, w, h, 0f, c)));
                    break;
                }
                default:
                    // «Вся комната, кроме островков»: заливка блоками 2×2 пикселя, кант у островков.
                    draws.Add((ord, () =>
                    {
                        var room = run.Room;
                        int x0 = Math.Max(0, (int)PX(0f)), x1 = Math.Min(img.W, (int)PX(room.W));
                        int y0 = Math.Max(0, (int)PY(0f)), y1 = Math.Min(img.H, (int)PY(room.H));
                        for (int y = y0; y < y1; y += 2)
                            for (int x = x0; x < x1; x += 2)
                            {
                                var p = new Vec2(camX + (x + 1f) / U, camY + (y + 1f) / U);
                                float best = float.MaxValue;
                                foreach (var s in z.Safe) best = Math.Min(best, Vec2.Distance(p, s) - z.SafeR);
                                if (best < 0f) continue;
                                img.Fill(x, y, 2, 2, c.r, c.g, c.b, c.a * (best < 0.2f ? 1f : 120f / 255f));
                            }
                    }));
                    break;
            }
        }

        private readonly List<FxDraw> _strike = new List<FxDraw>();

        /// <summary>Эффект удара зоны (StrikeFx – та же раскладка, что в игре): на полу над зоной и поверх существ.</summary>
        private void AddStrikeFx(Run run, Zone z, Frame img, List<(float order, Action act)> draws, Func<float, float> PX, Func<float, float> PY)
        {
            _strike.Clear();
            StrikeFx.Build(z, run.Room.W, run.Room.H, _strike);
            float U = _u;
            for (int i = 0; i < _strike.Count; i++)
            {
                var d = _strike[i];
                var spr = _f.Get(d.Key);
                if (spr?.texture == null) continue;
                float nw = spr.texture.width / spr.pixelsPerUnit, nh = spr.texture.height / spr.pixelsPerUnit;
                var col = ToColor(d.Tint);
                float px = PX(d.X), py = PY(d.Y), sx = d.W / nw, sy = d.H / nh, rot = d.Rot;
                draws.Add((d.Over ? 850f + i * 1e-4f : -63f + i * 1e-4f, () => img.Draw(spr, px, py, U, sx, sy, rot, col)));
            }
        }

        /// <summary>
        /// Темнота тёмных биомов и «Тишины» Шумового хора (как Hud.DrawDarknessMask): свет вокруг героя
        /// и горящих ламп, остальное почти чёрное. Маска считается блоками 4×4 пикселя.
        /// </summary>
        private void DrawDarkness(Run run, Frame img, float camX, float camY)
        {
            if (run.IsHub || run.Biome == null || (run.Biome.Rule != BiomeRule.Darkness && run.Blackout <= 0f)) return;
            float U = _u, h = img.H;
            var lights = new List<(float x, float y, float inner, float outer)>
            {
                ((run.Player.Pos.X - camX) * U, (run.Player.Pos.Y - camY) * U, h * 0.135f, h * 0.375f),
            };
            foreach (var l in run.LitLamps())
            {
                float outer = Run.LampLightRadius * U * (0.75f + 0.25f * l.Strength);
                lights.Add(((l.Pos.X - camX) * U, (l.Pos.Y - camY) * U, outer * 0.5f, outer));
            }
            const int B = 4;
            for (int y = 0; y < img.H; y += B)
                for (int x = 0; x < img.W; x += B)
                {
                    float a = 1f;
                    foreach (var L in lights)
                    {
                        float dx = x + B * 0.5f - L.x, dy = y + B * 0.5f - L.y;
                        float d = (float)Math.Sqrt(dx * dx + dy * dy);
                        float k = d <= L.inner ? 0f : d >= L.outer ? 1f : (d - L.inner) / (L.outer - L.inner);
                        if (k < a) a = k;
                        if (a <= 0f) break;
                    }
                    if (a > 0f) img.Fill(x, y, B, B, 2f / 255f, 4f / 255f, 12f / 255f, a * 250f / 255f);
                }
        }

        /// <summary>Тело многочастного босса в комнате (null – его нет).</summary>
        private static BossEntity ArenaBody(Run run)
        {
            foreach (var e in run.Room.Entities)
                if (!e.Dead && e is BossEntity b && b.Parts.Count > 0) return b;
            return null;
        }

        /// <summary>Полосы многочастного босса: ярус и по полоске с 10 делениями на каждую открытую часть.</summary>
        private static void DrawPartsHud(BossEntity body, Frame img, int s)
        {
            int bw = img.W * 6 / 10, bx = (img.W - bw) / 2, by = 10 * s, bh = 6 * s;
            // Ярус – по виду его частей (шрифт кадра – латиница).
            string tier = body.Parts.Where(p => p.Part.Tier == body.ActiveTier).Select(p => p.Part.Kind.ToString().ToUpperInvariant()).FirstOrDefault() ?? "";
            string head = body.BossDef.Key.ToUpperInvariant() + "  TIER " + body.ActiveTier + "/" + body.PhaseCount + ": " + tier;
            img.Text(head, bx, by, s, new Color(1f, 0.9f, 0.9f));
            by += 8 * s;
            var open = body.Parts.Where(p => !p.Dead && Run.PartOpen(p)).ToList();
            if (open.Count == 0) return;
            int gap = 6 * s, w = (bw - gap * (open.Count - 1)) / open.Count, x = bx;
            foreach (var p in open)
            {
                img.Fill(x - s, by - s, w + 2 * s, bh + 2 * s, 0.02f, 0.02f, 0.05f, 0.85f);
                float k = Math.Max(0f, Math.Min(1f, p.Hp / Math.Max(1f, p.MaxHp)));
                if (p.PhaseShield > 0f) img.Fill(x, by, (int)(w * k), bh, 1f, 1f, 1f);
                else img.Fill(x, by, (int)(w * k), bh, 0.92f, 0.16f, 0.3f);
                for (int i = 1; i < p.PhaseCount; i++) img.Fill(x + w * i / p.PhaseCount, by, s, bh, 0.02f, 0.02f, 0.05f, 0.8f);
                img.Text(p.Part.Key.ToUpperInvariant() + " " + (p.Phase + 1) + "/" + p.PhaseCount, x, by + bh + 3 * s, s, new Color(1f, 0.85f, 0.4f));
                x += w + gap;
            }
        }

        // ───────────── Статичный слой ─────────────

        private long StaticHash(Run run)
        {
            var room = run.Room;
            long h = room.W * 7919 + room.H;
            for (int y = 0; y < room.H; y++)
                for (int x = 0; x < room.W; x++)
                    h = h * 31 + (int)room.Tiles[x, y];
            foreach (var d in run.CurrentNode.Doors)
                h = h * 31 + (d.Hidden ? 1 : 0) + (run.IsDoorOpen(d) ? 2 : 0) + (d.Locked ? 4 : 0);
            return h;
        }

        private void DrawStatic(Run run, Frame img, float camX, float camY)
        {
            var room = run.Room;
            int U = _u;
            long hash = StaticHash(run);
            if (_static == null || _staticRoom != room || hash != _staticHash || _static.W != (room.W + 2) * _u)
            {
                _staticRoom = room;
                _staticHash = hash;
                _static = BuildStatic(run);
            }
            // Окно кэша (начало кэша – клетка (−1; −1) мира) копируется в кадр построчно.
            var bg = ToColor(run.Biome.FogColor);
            img.Clear(bg.r * 0.35f, bg.g * 0.35f, bg.b * 0.35f);
            int ox = (int)Math.Round((camX + 1f) * U), oy = (int)Math.Round((camY + 1f) * U);
            for (int y = 0; y < img.H; y++)
            {
                int sy = y + oy;
                if (sy < 0 || sy >= _static.H) continue;
                int sx0 = Math.Max(0, ox), sx1 = Math.Min(_static.W, ox + img.W);
                if (sx1 <= sx0) continue;
                Buffer.BlockCopy(_static.Rgb, (sy * _static.W + sx0) * 3, img.Rgb, (y * img.W + (sx0 - ox)) * 3, (sx1 - sx0) * 3);
            }
        }

        private Frame BuildStatic(Run run)
        {
            var room = run.Room;
            float U = _u;
            var img = new Frame((room.W + 2) * _u, (room.H + 2) * _u);
            var bg = ToColor(run.Biome.FogColor);
            img.Clear(bg.r * 0.35f, bg.g * 0.35f, bg.b * 0.35f);
            float PX(float x) => (x + 1f) * U;
            float PY(float y) => (y + 1f) * U;
            Color floor = Boost(ToColor(run.Biome.FloorColor)), wall = Boost(ToColor(run.Biome.WallColor)), accent = ToColor(run.Biome.AccentColor);
            for (int y = 0; y < room.H; y++)
                for (int x = 0; x < room.W; x++)
                {
                    float k = ((x + y) & 1) == 0 ? 1f : 0.93f;
                    img.Draw(_f.Get("floor"), PX(x + 0.5f), PY(y + 0.5f), U, 1, 1, 0, new Color(floor.r * k, floor.g * k, floor.b * k, 1));
                }
            for (int x = -1; x <= room.W; x++)
            {
                img.Draw(_f.Get("wall"), PX(x + 0.5f), PY(-0.5f), U, 1, 1, 0, wall);
                img.Draw(_f.Get("wall"), PX(x + 0.5f), PY(room.H + 0.5f), U, 1, 1, (float)Math.PI, wall);
            }
            for (int y = 0; y < room.H; y++)
            {
                img.Draw(_f.Get("wall"), PX(-0.5f), PY(y + 0.5f), U, 1, 1, -(float)Math.PI / 2, wall);
                img.Draw(_f.Get("wall"), PX(room.W + 0.5f), PY(y + 0.5f), U, 1, 1, (float)Math.PI / 2, wall);
            }
            foreach (var d in run.CurrentNode.Doors)
            {
                if (d.Hidden) continue;
                float ang = d.Side == Dir.Up ? 0 : d.Side == Dir.Down ? (float)Math.PI : d.Side == Dir.Left ? -(float)Math.PI / 2 : (float)Math.PI / 2;
                Color dc = d.Kind == DoorKind.Boss ? new Color(0.9f, 0.15f, 0.2f) : d.Kind == DoorKind.Treasure ? new Color(1f, 0.82f, 0.25f) : d.Kind == DoorKind.Shop ? new Color(0.35f, 0.9f, 0.45f) : accent;
                img.Draw(_f.Get(run.IsDoorOpen(d) ? "door" : "door_closed"), PX(d.LocalTile.X + 0.5f), PY(d.LocalTile.Y + 0.5f), U, 1, 1, ang, dc);
                if (d.Locked) img.Draw(_f.Get("lock_overlay"), PX(d.LocalTile.X + 0.5f), PY(d.LocalTile.Y + 0.5f), U, 1, 1, 0, Color.white);
            }
            Color light = Lerp(accent, Color.white, 0.25f);
            for (int y = 0; y < room.H; y++)
                for (int x = 0; x < room.W; x++)
                {
                    var t = room.Tiles[x, y];
                    if (t == TileType.Floor || t == TileType.Furniture) continue;
                    string key;
                    Color c = Color.white;
                    switch (t)
                    {
                        case TileType.Rock: key = "rock"; c = light; break;
                        case TileType.Metal: key = "metal"; c = Lerp(wall, Color.white, 0.2f); break;
                        case TileType.Junk: key = "junk"; c = Lerp(light, new Color(0.7f, 0.6f, 0.45f), 0.5f); break;
                        case TileType.Pit: key = "pit"; break;
                        case TileType.Spikes: key = "spikes"; break;
                        case TileType.Fire: key = "fire"; break;
                        case TileType.Tinted: key = "tinted"; c = Lerp(light, accent, 0.25f); break;
                        case TileType.Barrel: key = "barrel"; break;
                        case TileType.KeyBlock: key = "keyblock"; break;
                        default: key = "web"; break;
                    }
                    img.Draw(_f.Get(key), PX(x + 0.5f), PY(y + 0.5f), U, 1, 1, 0, c);
                }
            return img;
        }

        // ───────────── Листы студии ─────────────

        private Sprite ArtFrame(string key, Clip clip, int frame)
        {
            string id = key + "#" + clip + "#" + frame;
            if (_art.TryGetValue(id, out var sp)) return sp;
            var sheet = ArtLibrary.Get(key);
            if (sheet == null) return null;
            if (!sheet.Has(clip)) clip = Clips.IsBossAttack(clip) && sheet.Has(Clip.Attack) ? Clip.Attack : Clip.Idle;
            var frames = sheet.Frames[clip];
            var img = frames[frame % frames.Length];
            var t = new Texture2D(img.W, img.H, TextureFormat.RGBA32, false);
            for (int y = 0; y < img.H; y++)
                for (int x = 0; x < img.W; x++)
                {
                    var c = img.Px[y * img.W + x];
                    t.Pixels[(img.H - 1 - y) * img.W + x] = new Color32(c.R, c.G, c.B, c.A);
                }
            sp = Sprite.Create(t, new Rect(0, 0, img.W, img.H), new Vector2(0.5f, 0.5f), ArtSheet.PixelsPerUnit * sheet.NativeScale, 0, SpriteMeshType.FullRect);
            _art[id] = sp;
            return sp;
        }

        private Sprite StudioSprite(Run run, Entity e)
        {
            if (e.Type == EntityType.Player)
            {
                var p = (PlayerEntity)e;
                var clip = p.DashTime > 0 ? Clip.Dash : p.Vel.LengthSq > 0.5f ? Clip.Move : Clip.Idle;
                return ArtFrame("char:" + run.Character.Key, clip, (int)(e.Age * 10f));
            }
            if (e is BossEntity b)
            {
                string key = b.Part != null ? ArtLibrary.PartKeyFor(b.BossDef, b.Part) : b.IsSegment ? ArtLibrary.SegmentKeyFor(b.BossDef) : "boss:" + b.BossDef.Key;
                var clip = b.InAttack ? Clips.Of(b.CurrentAttack) : b.Telegraph > 0.05f ? Clip.Attack : b.Raging ? Clip.Rage : b.Vel.LengthSq > 0.3f ? Clip.Move : Clip.Idle;
                if (b.Spawned > 0f) return ArtFrame(key, Clip.Spawn, (int)((1f - Math.Min(1f, b.Spawned / 1.4f)) * 7.99f));
                // Тело вторит части, которая сейчас бьёт: на его экране – знак этой атаки.
                if (b.Parts.Count > 0 && !b.InAttack)
                    foreach (var p in b.Parts)
                        if (!p.Dead && p.InAttack) { clip = Clips.Of(p.CurrentAttack); break; }
                return ArtFrame(key, clip, (int)(e.Age * 8f));
            }
            if (e is EnemyEntity en && en.Def != null)
            {
                var clip = en.Telegraph > 0.05f ? Clip.Attack : en.Vel.LengthSq > 0.05f ? Clip.Move : Clip.Idle;
                return ArtFrame("enemy:" + en.Def.Key, clip, (int)(e.Age * 8f) + en.Id);
            }
            return null;
        }

        // ───────────── HUD ─────────────

        private void DrawHud(Run run, Frame img)
        {
            if (run.IsHub) return;
            int s = Math.Max(1, _base / 32);
            int hx = 10 * s;
            for (int i = 0; i < run.Health.Containers; i++)
            {
                int filled = run.Health.Red - i * 2;
                img.Draw(_f.Get(filled >= 2 ? "heart" : filled == 1 ? "heart_half" : "heart_empty"), hx + 10 * s, 14 * s, 24f * s, 1, 1, 0, Color.white);
                hx += 20 * s;
            }
            for (int i = 0; i < run.Health.Extra.Count; i += 2)
            {
                img.Draw(_f.Get(run.Health.Extra[i] == ExtraHeart.Black ? "black_heart" : "soul_heart"), hx + 10 * s, 14 * s, 24f * s, 1, 1, 0, Color.white);
                hx += 20 * s;
            }

            // Полоса здоровья босса с делениями фаз.
            BossEntity boss = null;
            foreach (var e in run.Room.Entities)
                if (e is BossEntity b && !b.IsSegment && !b.Dead) { boss = b; break; }
            if (boss == null) return;
            if (boss.Parts.Count > 0)
            {
                DrawPartsHud(boss, img, s);
                return;
            }
            // Сверху по центру: снизу слева – панель музыки конвейера.
            int bw = img.W * 5 / 10, bh = 7 * s, bx = (img.W - bw) / 2, by = 10 * s;
            img.Fill(bx - s, by - s, bw + 2 * s, bh + 2 * s, 0.02f, 0.02f, 0.05f, 0.85f);
            float k = Math.Max(0f, Math.Min(1f, boss.Hp / Math.Max(1f, boss.MaxHp)));
            img.Fill(bx, by, (int)(bw * k), bh, 0.92f, 0.16f, 0.3f);
            for (int p = 1; p < boss.PhaseCount; p++) img.Fill(bx + bw * p / boss.PhaseCount, by, s, bh, 0.02f, 0.02f, 0.05f, 0.8f);
            string name = boss.BossDef.Key.ToUpperInvariant() + "  " + (boss.Phase + 1) + "/" + boss.PhaseCount;
            img.Text(name, bx, by + bh + 4 * s, s, new Color(1f, 0.9f, 0.9f));
        }
    }
}
