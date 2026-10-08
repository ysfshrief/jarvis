using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Jarvis.Core.AI;
using Jarvis.Core.Persistence;
using Jarvis.Core.Settings;
using Microsoft.Extensions.Logging;

namespace Jarvis.Core.Memory;

public sealed record SemanticStatus(bool Available, string? Model, string? Provider, int Indexed, int Total, string Message);

/// <summary>
/// Meaning-based search: texts are turned into vectors by a local embedding model (Ollama, e.g.
/// bge-m3 — multilingual, good Arabic) and compared by cosine similarity. Without an embedding model
/// everything still works with keyword search; the status says so instead of pretending.
/// </summary>
public sealed class SemanticIndex(JarvisDatabase db, ProviderRegistry providers, ISettingsStore settings, ILogger<SemanticIndex> logger)
{
    private readonly Channel<(string Type, string Id, string Text)> _queue = Channel.CreateUnbounded<(string, string, string)>();
    private (IEmbeddingProvider Provider, string ProviderId, string Model)? _model;
    private DateTimeOffset _resolvedAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _resolveLock = new(1, 1);

    /// <summary>Finds an installed embedding model (the configured one first). Cached for two minutes.</summary>
    public async Task<(IEmbeddingProvider Provider, string ProviderId, string Model)?> ResolveAsync(CancellationToken ct)
    {
        if (DateTimeOffset.Now - _resolvedAt < TimeSpan.FromMinutes(2)) return _model;
        await _resolveLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.Now - _resolvedAt < TimeSpan.FromMinutes(2)) return _model;
            _model = null;
            var wanted = settings.Current.Ai.EmbeddingModel?.Trim() ?? "";
            if (wanted.Length > 0)
            {
                foreach (var (id, p) in providers.Providers)
                {
                    if (p is not IEmbeddingProvider emb || p is not IModelCatalog cat || !p.IsLocal) continue;
                    IReadOnlyList<ModelInfo> models;
                    try { models = await cat.ListModelsAsync(ct).ConfigureAwait(false); }
                    catch (Exception ex) when (ex is HttpRequestException or AiProviderException or TaskCanceledException or System.Text.Json.JsonException) { continue; }
                    var match = models.FirstOrDefault(m => m.Has(ModelCapabilities.Embedding) &&
                        (m.Name.Equals(wanted, StringComparison.OrdinalIgnoreCase) || m.Name.Equals(wanted + ":latest", StringComparison.OrdinalIgnoreCase)))
                        ?? models.FirstOrDefault(m => m.Has(ModelCapabilities.Embedding));
                    if (match is not null) { _model = (emb, id, match.Name); break; }
                }
            }
            _resolvedAt = DateTimeOffset.Now;
            return _model;
        }
        finally { _resolveLock.Release(); }
    }

    public void Invalidate() => _resolvedAt = DateTimeOffset.MinValue;

    /// <summary>Queue a text for (re)indexing; the background worker embeds it when a model is available.</summary>
    public void Enqueue(string ownerType, string ownerId, string text) => _queue.Writer.TryWrite((ownerType, ownerId, text));

    public void Remove(string ownerType, string ownerId)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "DELETE FROM embeddings WHERE owner_type = $t AND owner_id = $id;";
        cmd.Parameters.AddWithValue("$t", ownerType);
        cmd.Parameters.AddWithValue("$id", ownerId);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Embeds up to one batch of queued texts. Returns how many queue items were processed (0 = nothing to do or no model).</summary>
    public async Task<int> DrainAsync(CancellationToken ct)
    {
        var batch = new List<(string Type, string Id, string Text)>();
        while (batch.Count < 32 && _queue.Reader.TryRead(out var item)) batch.Add(item);
        if (batch.Count == 0) return 0;
        var model = await ResolveAsync(ct).ConfigureAwait(false);
        if (model is null) return 0; // dropped; the backfill picks them up once a model exists
        var todo = batch.Where(b => NeedsIndex(b.Type, b.Id, Hash(b.Text), model.Value.Model)).ToList();
        if (todo.Count > 0)
        {
            var vectors = await model.Value.Provider.EmbedAsync(model.Value.Model, todo.Select(t => Clip(t.Text)).ToList(), ct).ConfigureAwait(false);
            for (var i = 0; i < todo.Count && i < vectors.Length; i++) Store(todo[i].Type, todo[i].Id, model.Value.Model, Hash(todo[i].Text), vectors[i]);
        }
        return batch.Count;
    }

    /// <summary>Waits for queued work (used by the background worker).</summary>
    public ValueTask<bool> WaitForWorkAsync(CancellationToken ct) => _queue.Reader.WaitToReadAsync(ct);

    /// <summary>Most similar items of a type. Empty when no model is available.</summary>
    public async Task<IReadOnlyList<(string Id, double Score)>> SearchAsync(string ownerType, string query, int top, CancellationToken ct)
    {
        var model = await ResolveAsync(ct).ConfigureAwait(false);
        if (model is null || string.IsNullOrWhiteSpace(query)) return [];
        var q = (await model.Value.Provider.EmbedAsync(model.Value.Model, [Clip(query)], ct).ConfigureAwait(false)).FirstOrDefault();
        if (q is null || q.Length == 0) return [];
        var results = new List<(string, double)>();
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT owner_id, vector FROM embeddings WHERE owner_type = $t AND model = $m;";
        cmd.Parameters.AddWithValue("$t", ownerType);
        cmd.Parameters.AddWithValue("$m", model.Value.Model);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var v = FromBytes((byte[])r[1]);
            if (v.Length == q.Length) results.Add((r.GetString(0), Cosine(q, v)));
        }
        return results.OrderByDescending(x => x.Item2).Take(top).ToList();
    }

    public async Task<SemanticStatus> StatusAsync(string ownerType, int total, CancellationToken ct)
    {
        var model = await ResolveAsync(ct).ConfigureAwait(false);
        int indexed;
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT COUNT(*) FROM embeddings WHERE owner_type = $t AND ($m IS NULL OR model = $m);";
            cmd.Parameters.AddWithValue("$t", ownerType);
            cmd.Parameters.AddWithValue("$m", (object?)model?.Model ?? DBNull.Value);
            indexed = Convert.ToInt32(cmd.ExecuteScalar());
        }
        if (model is null)
        {
            var wanted = settings.Current.Ai.EmbeddingModel;
            return new SemanticStatus(false, null, null, 0, total, string.IsNullOrWhiteSpace(wanted)
                ? "Semantic search is off (no embedding model selected). Keyword search is used."
                : $"Semantic search needs the free “{wanted}” model — download it in Settings → AI. Keyword search is used meanwhile.");
        }
        return new SemanticStatus(true, model.Value.Model, model.Value.ProviderId, indexed, total,
            indexed >= total ? $"Meaning-based search is on ({model.Value.Model})." : $"Indexing… {indexed} of {total} ({model.Value.Model}).");
    }

    /// <summary>Owner ids of a type that have no vector for the current model.</summary>
    public IReadOnlyList<string> Missing(string ownerType, IEnumerable<string> ids, string model)
    {
        var have = new HashSet<string>();
        using (var conn = db.Open())
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT owner_id FROM embeddings WHERE owner_type = $t AND model = $m;";
            cmd.Parameters.AddWithValue("$t", ownerType);
            cmd.Parameters.AddWithValue("$m", model);
            using var r = cmd.ExecuteReader();
            while (r.Read()) have.Add(r.GetString(0));
        }
        return ids.Where(i => !have.Contains(i)).ToList();
    }

    private bool NeedsIndex(string type, string id, string hash, string model)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM embeddings WHERE owner_type = $t AND owner_id = $id AND hash = $h AND model = $m;";
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$m", model);
        return cmd.ExecuteScalar() is null;
    }

    private void Store(string type, string id, string model, string hash, float[] vector)
    {
        using var conn = db.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO embeddings (owner_type, owner_id, model, dims, hash, vector, updated_at) VALUES ($t, $id, $m, $d, $h, $v, $now)
            ON CONFLICT(owner_type, owner_id) DO UPDATE SET model = excluded.model, dims = excluded.dims, hash = excluded.hash, vector = excluded.vector, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("$t", type);
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$m", model);
        cmd.Parameters.AddWithValue("$d", vector.Length);
        cmd.Parameters.AddWithValue("$h", hash);
        cmd.Parameters.AddWithValue("$v", ToBytes(vector));
        cmd.Parameters.AddWithValue("$now", JarvisDatabase.Now());
        cmd.ExecuteNonQuery();
    }

    internal static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }
        return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }

    private static string Clip(string text) => text.Length > 2000 ? text[..2000] : text;
    private static string Hash(string text) => Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)));

    private static byte[] ToBytes(float[] v)
    {
        var bytes = new byte[v.Length * 4];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBytes(byte[] b)
    {
        var v = new float[b.Length / 4];
        Buffer.BlockCopy(b, 0, v, 0, b.Length);
        return v;
    }
}
