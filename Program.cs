            }
            catch { /* Ignore transient send errors */ }
        }
    }
    private static ServerSnake SpawnSnake(int id, string name, string skinId, bool isBot)
    {
        var s = new ServerSnake { Id = id, Name = name, SkinId = skinId, IsBot = isBot };
        RespawnSnake(s, 10f);
        return s;
    }
    private static void RespawnSnake(ServerSnake s, float startMass)
    {
        var rng = Random.Shared;
        s.IsAlive = true;
        s.Mass = startMass;
        s.Kills = 0;
        s.LastKillerName = "";
        s.AngleDeg = (float)(rng.NextDouble() * 360.0);
        s.TargetAngleDeg = s.AngleDeg;
        s.Head = new Vec2((float)(rng.NextDouble() * 40 - 20), (float)(rng.NextDouble() * 40 - 20));
        s.Segments.Clear();
        for (int i = 0; i < 6; i++) s.Segments.Add(s.Head);
    }
    private static void SeedPellets(int count)
    {
        var rng = Random.Shared;
        for (int i = 0; i < count; i++)
        {
            double a = rng.NextDouble() * Math.PI * 2;
            double r = Math.Sqrt(rng.NextDouble()) * (ArenaRadius - 2f);
            Pellets.Add(new PelletSnapshot
            {
                id = Interlocked.Increment(ref _nextPelletId),
                pos = new Vec2((float)(Math.Cos(a) * r), (float)(Math.Sin(a) * r)),
                mass = 1f,
                isDeathDrop = false
            });
        }
    }
    private static float MoveTowardsAngle(float current, float target, float maxDelta)
    {
        float diff = ((target - current + 540f) % 360f) - 180f;
        if (MathF.Abs(diff) <= maxDelta) return target;
        return current + MathF.Sign(diff) * maxDelta;
    }
}
