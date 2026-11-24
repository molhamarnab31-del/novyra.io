using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace SnakeIO.Server;

public struct Vec2
{
    public float x { get; set; }
    public float y { get; set; }
    public Vec2(float x, float y) { this.x = x; this.y = y; }
    public static float DistSq(Vec2 a, Vec2 b) => (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y);
}

public class ClientInputPacket
{
    public string type { get; set; } = "input"; // "join", "input", "respawn"
    public int playerId { get; set; }
    public string playerName { get; set; } = "Player";
    public string skinId { get; set; } = "classic_green";
    public float targetAngleDeg { get; set; }
    public bool wantsBoost { get; set; }
    public float preserveMass { get; set; }
    public uint sequence { get; set; }
}

public class SnakeSnapshot
{
    public int id { get; set; }
    public string name { get; set; } = "";
    public string skinId { get; set; } = "classic_green";
    public float mass { get; set; }
    public float angleDeg { get; set; }
    public bool isBoosting { get; set; }
    public int kills { get; set; }
    public bool isAlive { get; set; }
    public Vec2[] segments { get; set; } = Array.Empty<Vec2>();
}

public class PelletSnapshot
{
    public int id { get; set; }
    public Vec2 pos { get; set; }
    public float mass { get; set; }
    public bool isDeathDrop { get; set; }
}

public class WorldSnapshotPacket
{
    public float serverTime { get; set; }
    public int yourPlayerId { get; set; }
    public string killerName { get; set; } = "";
    public List<SnakeSnapshot> snakes { get; set; } = new();
    public List<PelletSnapshot> pellets { get; set; } = new();
}

internal class ServerSnake
{
    public int Id;
    public string Name = "";
    public string SkinId = "classic_green";
    public bool IsBot;
    public Vec2 Head;
    public float AngleDeg;
    public float TargetAngleDeg;
    public float Mass = 10f;
    public bool WantsBoost;
    public bool IsAlive = true;
    public int Kills;
    public string LastKillerName = "";
    public float RespawnAt;
    public float NextAiDecisionAt;
    public readonly List<Vec2> Segments = new();
}

internal class ClientSession
{
    public int PlayerId;
    public WebSocket Socket = null!;
    public float TargetAngleDeg;
    public bool WantsBoost;
}

internal static class Program
{
    private const float ArenaRadius = 60f;
    private const float ViewRadiusSq = 32f * 32f;
    private const int TargetPelletCount = 380;
    private const int BotCount = 14;

    private static readonly string[] BotNames =
    {
        "Viper", "Cobra", "Python", "PandaBot", "AnkyRex", "Blaze",
        "Shadow", "Neon", "Titan", "Frost", "Ghost", "Nova", "Storm", "Orbit"
    };

    private static readonly string[] BotSkins =
    {
        "classic_green", "cobalt_cobra", "sunny_orange", "crimson_fang",
        "anky_dino", "panda_bear", "moo_cow", "parrot_macaw",
        "fluffy_bunny", "blaze_fire", "frost_ice", "cyber_neon"
    };

    private static readonly ConcurrentDictionary<int, ClientSession> Clients = new();
    private static readonly Dictionary<int, ServerSnake> Snakes = new();
    private static readonly List<PelletSnapshot> Pellets = new();
    private static int _nextPlayerId = 100;
    private static int _nextPelletId = 1;
    private static float _serverTime;

    public static async Task Main(string[] args)
    {
        string port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
        string prefix = Environment.GetEnvironmentVariable("WS_PREFIX") ?? $"http://+:{port}/ws/";
        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);
        try
        {
            listener.Start();
        }
        catch (HttpListenerException)
        {
            // Fallback for non-admin Windows local testing
            listener.Prefixes.Clear();
            prefix = $"http://localhost:{port}/ws/";
            listener.Prefixes.Add(prefix);
            listener.Start();
        }
        Console.WriteLine($"[SnakeIO.Server] Hybrid Arena listening on {prefix}");

        SeedPellets(TargetPelletCount);
        SeedBots();

        _ = Task.Run(() => AcceptLoop(listener));

        const float dt = 1f / 30f;
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(33));
        while (await timer.WaitForNextTickAsync())
        {
            _serverTime += dt;
            StepSimulation(dt);
            await BroadcastSnapshots();
        }
    }

    private static void SeedBots()
    {
        for (int i = 1; i <= BotCount; i++)
        {
            var bot = SpawnSnake(i, BotNames[i % BotNames.Length], BotSkins[i % BotSkins.Length], true);
            Snakes[i] = bot;
        }
    }

    private static async Task AcceptLoop(HttpListener listener)
    {
        while (listener.IsListening)
        {
            var ctx = await listener.GetContextAsync();
            if (!ctx.Request.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = 400;
                ctx.Response.Close();
                continue;
            }

            var wsCtx = await ctx.AcceptWebSocketAsync(null);
            int pid = Interlocked.Increment(ref _nextPlayerId);
            var session = new ClientSession { PlayerId = pid, Socket = wsCtx.WebSocket };
            Clients[pid] = session;

            lock (Snakes)
            {
                Snakes[pid] = SpawnSnake(pid, $"Player_{pid}", "classic_green", false);
            }

            Console.WriteLine($"[SnakeIO.Server] Player {pid} joined.");
            _ = Task.Run(() => ReceiveLoop(session));
        }
    }

    private static async Task ReceiveLoop(ClientSession session)
    {
        var buffer = new byte[4096];
        try
        {
            while (session.Socket.State == WebSocketState.Open)
            {
                var res = await session.Socket.ReceiveAsync(buffer, CancellationToken.None);
                if (res.MessageType == WebSocketMessageType.Close) break;

                string json = Encoding.UTF8.GetString(buffer, 0, res.Count);
                var pkt = JsonSerializer.Deserialize<ClientInputPacket>(json);
                if (pkt == null) continue;

                if (pkt.type == "join")
                {
                    lock (Snakes)
                    {
                        if (Snakes.TryGetValue(session.PlayerId, out var s))
                        {
                            s.Name = string.IsNullOrWhiteSpace(pkt.playerName) ? s.Name : pkt.playerName;
                            s.SkinId = string.IsNullOrWhiteSpace(pkt.skinId) ? "classic_green" : pkt.skinId;
                        }
                    }
                }
                else if (pkt.type == "respawn")
                {
                    lock (Snakes)
                    {
                        if (Snakes.TryGetValue(session.PlayerId, out var s))
                        {
                            RespawnSnake(s, pkt.preserveMass > 0f ? pkt.preserveMass : 10f);
                            if (!string.IsNullOrWhiteSpace(pkt.skinId)) s.SkinId = pkt.skinId;
                        }
                    }
                }
                else
                {
                    session.TargetAngleDeg = pkt.targetAngleDeg;
                    session.WantsBoost = pkt.wantsBoost;
                }
            }
        }
        catch { /* Client disconnected */ }
        finally
        {
            Clients.TryRemove(session.PlayerId, out _);
            lock (Snakes) { Snakes.Remove(session.PlayerId); }
            Console.WriteLine($"[SnakeIO.Server] Player {session.PlayerId} left.");
        }
    }

    private static void StepSimulation(float dt)
    {
        lock (Snakes)
        {
            foreach (var s in Snakes.Values)
            {
                if (!s.IsAlive)
                {
                    if (s.IsBot && _serverTime >= s.RespawnAt)
                    {
                        RespawnSnake(s, 10f);
                    }
                    continue;
                }

                if (s.IsBot)
                {
                    UpdateBotAI(s);
                }
                else if (Clients.TryGetValue(s.Id, out var client))
                {
                    s.TargetAngleDeg = client.TargetAngleDeg;
                    s.WantsBoost = client.WantsBoost && s.Mass >= 15f;
                }

                s.AngleDeg = MoveTowardsAngle(s.AngleDeg, s.TargetAngleDeg, 260f * dt);
                float speed = s.WantsBoost ? 12.5f : 6.5f;
                if (s.WantsBoost) s.Mass = Math.Max(10f, s.Mass - 4f * dt);

                float rad = s.AngleDeg * MathF.PI / 180f;
                s.Head = new Vec2(s.Head.x + MathF.Cos(rad) * speed * dt, s.Head.y + MathF.Sin(rad) * speed * dt);

                s.Segments.Insert(0, s.Head);
                int maxSegs = Math.Clamp(6 + (int)((s.Mass - 10f) / 2.5f), 6, 180);
                while (s.Segments.Count > maxSegs) s.Segments.RemoveAt(s.Segments.Count - 1);

                // Eat pellets
                for (int i = Pellets.Count - 1; i >= 0; i--)
                {
                    if (Vec2.DistSq(s.Head, Pellets[i].pos) <= 1.25f)
                    {
                        s.Mass += Pellets[i].mass;
                        Pellets.RemoveAt(i);
                    }
                }
            }

            // Collisions
            foreach (var s in Snakes.Values)
            {
                if (!s.IsAlive) continue;

                if (s.Head.x * s.Head.x + s.Head.y * s.Head.y > (ArenaRadius - 0.5f) * (ArenaRadius - 0.5f))
                {
                    KillSnake(s, null);
                    continue;
                }

                foreach (var other in Snakes.Values)
                {
                    if (!other.IsAlive || other.Id == s.Id) continue;
                    bool hit = false;
                    for (int seg = 0; seg < other.Segments.Count; seg++)
                    {
                        if (Vec2.DistSq(s.Head, other.Segments[seg]) <= 0.72f)
                        {
                            hit = true;
                            break;
                        }
                    }
                    if (hit)
                    {
                        KillSnake(s, other);
                        break;
                    }
                }
            }

            if (Pellets.Count < TargetPelletCount) SeedPellets(TargetPelletCount - Pellets.Count);
        }
    }

    private static void UpdateBotAI(ServerSnake bot)
    {
        if (_serverTime < bot.NextAiDecisionAt) return;
        bot.NextAiDecisionAt = _serverTime + 0.14f;

        float distSq = bot.Head.x * bot.Head.x + bot.Head.y * bot.Head.y;
        if (distSq > (ArenaRadius - 8f) * (ArenaRadius - 8f))
        {
            bot.TargetAngleDeg = MathF.Atan2(-bot.Head.y, -bot.Head.x) * 180f / MathF.PI;
            bot.WantsBoost = false;
            return;
        }

        // Seek nearest pellet
        float bestDistSq = 180f;
        Vec2 target = bot.Head;
        bool found = false;
        for (int i = 0; i < Pellets.Count; i += 2)
        {
            float dSq = Vec2.DistSq(bot.Head, Pellets[i].pos);
            if (Pellets[i].isDeathDrop) dSq *= 0.35f;
            if (dSq < bestDistSq)
            {
                bestDistSq = dSq;
                target = Pellets[i].pos;
                found = true;
            }
        }

        bot.TargetAngleDeg = found
            ? MathF.Atan2(target.y - bot.Head.y, target.x - bot.Head.x) * 180f / MathF.PI
            : bot.AngleDeg + (float)(Random.Shared.NextDouble() * 50.0 - 25.0);
        bot.WantsBoost = false;
    }

    private static void KillSnake(ServerSnake victim, ServerSnake? killer)
    {
        victim.IsAlive = false;
        victim.WantsBoost = false;
        victim.LastKillerName = killer != null ? killer.Name : "";
        if (killer != null) killer.Kills++;

        for (int i = 0; i < victim.Segments.Count; i += 2)
        {
            Pellets.Add(new PelletSnapshot
            {
                id = Interlocked.Increment(ref _nextPelletId),
                pos = victim.Segments[i],
                mass = 4f,
                isDeathDrop = true
            });
        }

        if (victim.IsBot) victim.RespawnAt = _serverTime + 2.5f;
    }

    private static async Task BroadcastSnapshots()
    {
        foreach (var client in Clients.Values)
        {
            if (client.Socket.State != WebSocketState.Open) continue;

            WorldSnapshotPacket packet;
            lock (Snakes)
            {
                if (!Snakes.TryGetValue(client.PlayerId, out var observer)) continue;
                packet = new WorldSnapshotPacket
                {
                    serverTime = _serverTime,
                    yourPlayerId = client.PlayerId,
                    killerName = observer.IsAlive ? "" : observer.LastKillerName
                };

                foreach (var s in Snakes.Values)
                {
                    if (!s.IsAlive && s.Id != client.PlayerId) continue;
                    if (s.Id != client.PlayerId && Vec2.DistSq(observer.Head, s.Head) > ViewRadiusSq) continue;

                    packet.snakes.Add(new SnakeSnapshot
                    {
                        id = s.Id,
                        name = s.Name,
                        skinId = s.SkinId,
                        mass = s.Mass,
                        angleDeg = s.AngleDeg,
                        isBoosting = s.WantsBoost,
                        kills = s.Kills,
                        isAlive = s.IsAlive,
                        segments = s.Segments.ToArray()
                    });
                }

                foreach (var p in Pellets)
                {
                    if (Vec2.DistSq(observer.Head, p.pos) <= ViewRadiusSq) packet.pellets.Add(p);
                }
            }

            byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(packet));
            try
            {
                await client.Socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None);
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
