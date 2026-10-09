using System;
using System.Linq;
using Tokenfall.Core;
using Tokenfall.Core.Generation;
using Tokenfall.Core.Simulation;

namespace Conveer
{
    /// <summary>
    /// Бот-уклонист для витрины атак (тот же, что в BossAttackTests): не стреляет, а только уходит от опасности –
    /// зон (чем ближе удар, тем страшнее), пуль с упреждением на полсекунды, лучей и касания врагов.
    /// Так в видео видно, что от каждой атаки можно увернуться и где у неё проход.
    /// </summary>
    public sealed class Dodger
    {
        private static readonly Vec2[] Dirs = Enumerable.Range(0, 16).Select(i => Vec2.FromAngle(i * MathUtil.Tau / 16f)).Prepend(Vec2.Zero).ToArray();
        private Vec2 _last;

        private static float Danger(Run run, Vec2 p)
        {
            float d = 0f;
            var room = run.Room;
            if (p.X < 0.5f || p.Y < 0.5f || p.X > room.W - 0.5f || p.Y > room.H - 0.5f) d += 30f;
            foreach (var e in room.Entities)
            {
                if (e.Dead) continue;
                switch (e.Type)
                {
                    case EntityType.Zone:
                    {
                        var z = (Zone)e;
                        if (z.Contains(p, 0.45f)) d += z.Struck ? 200f : 40f + 160f * z.WarnProgress;
                        break;
                    }
                    case EntityType.Projectile:
                    {
                        var pr = (Projectile)e;
                        if (pr.FromPlayer) break;
                        for (float t = 0f; t <= 0.45f; t += 0.05f)
                        {
                            var q = pr.Pos + pr.Vel * t;
                            float r = pr.Radius + 0.55f;
                            if (Vec2.DistanceSq(q, p) < r * r) { d += 60f / (1f + t * 4f); break; }
                        }
                        break;
                    }
                    case EntityType.Beam:
                    {
                        var bm = (Beam)e;
                        if (bm.FromPlayer) break;
                        Vec2 end = bm.Pos + bm.Dir * bm.Length;
                        if (MathUtil.DistanceToSegment(p, bm.Pos, end) < bm.Width * 0.5f + 0.6f) d += bm.Warmup > 0.35f ? 30f : 150f;
                        break;
                    }
                    case EntityType.Enemy:
                    case EntityType.Boss:
                    {
                        var en = (EnemyEntity)e;
                        if (en.Contact && en.Visible && Vec2.Distance(en.Pos, p) < en.Radius + 0.9f) d += 50f;
                        break;
                    }
                }
            }
            if (!room.IsWalkable(RoomRuntime.ToTile(p))) d += 300f;
            d += 0.15f * Vec2.Distance(p, room.Center);
            return d;
        }

        public InputFrame Next(Run run)
        {
            var p = run.Player.Pos;
            float best = float.MaxValue;
            Vec2 pick = Vec2.Zero;
            foreach (var dir in Dirs)
            {
                float worst = 0f;
                // Ближние точки пути весят меньше: пройти краем зоны, которая ещё не ударила, можно – стоять в ней нельзя.
                worst = Math.Max(0.6f * Danger(run, p + dir * 4.5f * 0.12f), Math.Max(0.85f * Danger(run, p + dir * 4.5f * 0.28f), Danger(run, p + dir * 4.5f * 0.45f)));
                // Лёгкая инерция: при равной опасности бот не дёргается туда-сюда каждый кадр.
                if (Vec2.DistanceSq(dir, _last) < 1e-6f) worst -= 0.5f;
                if (worst < best - 0.01f) { best = worst; pick = dir; }
            }
            _last = pick;
            // Смотрит на босса – так видно, куда он целится.
            Vec2 aim = Vec2.Zero;
            foreach (var e in run.Room.Entities)
                if (e is BossEntity b && !b.Dead && !b.IsSegment) { aim = (b.Pos - p).Normalized; break; }
            return new InputFrame { Move = pick, Aim = aim };
        }
    }
}
