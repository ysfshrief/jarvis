using Jarvis.Core.Activity;
using Jarvis.Core.AI;
using Jarvis.Core.Memory;
using Jarvis.Core.Settings;
using Jarvis.Core.Tasks;

namespace Jarvis.Core.Tests;

public class KnowledgeTests
{
    [Fact]
    public async Task Explicit_memories_record_where_they_came_from()
    {
        using var host = new TestHost();
        var r = await host.Say("remember that the CityCrep meeting is on Sunday");
        Assert.True(r.Success);
        var m = host.Get<MemoryStore>().List().Single();
        Assert.Equal(MemorySources.UserExplicit, m.Source);
        Assert.True(m.IsConfirmed);
        Assert.Equal("text", m.Provenance!.Via);
        Assert.Equal("test-conv", m.Provenance.ConversationId);
        Assert.Contains("CityCrep meeting", m.Provenance.Quote);
    }

    [Fact]
    public async Task Memories_the_ai_decides_to_keep_on_its_own_stay_unconfirmed()
    {
        using var host = new TestHost(withModel: true);
        host.Model.CallTool("memory_remember", new { content = "The user seems to prefer tea", kind = "preference" }).Reply("ok");
        await host.Say("I'll have a tea while we plan the week, what's first on the agenda");
        var m = host.Get<MemoryStore>().List().Single();
        Assert.Equal(MemorySources.Derived, m.Source);
        Assert.False(m.IsConfirmed);
        Assert.NotNull(m.Provenance!.Reason);

        host.Model.CallTool("memory_remember", new { content = "The user's favourite colour is teal", kind = "preference" }).Reply("ok");
        await host.Say("please remember my favourite colour is teal");
        Assert.Contains(host.Get<MemoryStore>().List(), x => x.Content.Contains("teal") && x.Source == MemorySources.UserExplicit);
    }

    [Fact]
    public async Task Relationships_link_people_organisations_tasks_and_memories()
    {
        using var host = new TestHost();
        var r = await host.Say("Ahmed works at CityCrep");
        Assert.True(r.Success, r.Reply);
        await host.Say("أحمد شغال في سيتي كريب");
        host.Get<TaskStore>().Create(new NewTask("Send the CityCrep proposal"));
        await host.Say("remember that CityCrep wants the proposal by Thursday");

        var entities = host.Get<EntityStore>();
        var city = entities.Find("CityCrep")!;
        Assert.Equal(EntityTypes.Organization, city.Type);
        var profile = host.Get<KnowledgeService>().Profile(city);
        Assert.Contains(profile.Relations, x => x.FromName == "Ahmed" && x.Type == "works_at");
        Assert.Contains(profile.Memories, x => x.Content.Contains("Thursday"));   // linked because it mentions CityCrep
        Assert.Contains(profile.Tasks, t => t.Title == "Send the CityCrep proposal");

        var answer = await host.Say("what do you know about CityCrep");
        Assert.Contains("Ahmed works at CityCrep", answer.Reply);
        Assert.Contains("Thursday", answer.Reply);
        Assert.Contains("Send the CityCrep proposal", answer.Reply);

        var who = await host.Say("who is Ahmed");
        Assert.Contains("works at CityCrep", who.Reply);
    }

    [Fact]
    public async Task Who_is_falls_back_to_ai_for_unknown_people()
    {
        using var host = new TestHost(withModel: true);
        host.Model.Reply("Ada Lovelace was a mathematician.");
        var r = await host.Say("who is Ada Lovelace");
        Assert.Equal("ai", r.Route);
        Assert.Contains("mathematician", r.Reply);
    }

    [Fact]
    public void Learner_finds_routines_and_style_and_is_opt_in()
    {
        using var host = new TestHost(s => s.Memory.LearnPatterns = true);
        var log = host.Get<ActivityLog>();
        var start = DateTimeOffset.Now.Date.AddDays(-6).AddHours(9).AddMinutes(10);
        // Opened VS Code at ~9am on five days; asked for brevity three times.
        for (var d = 0; d < 5; d++)
            log.Record(ActivityKinds.Tool, "Open VS Code", "app_open", "Safe", "ok");
        var entries = Enumerable.Range(0, 5)
            .Select(d => new ActivityEntry(d, new DateTimeOffset(start.AddDays(d)), ActivityKinds.Tool, "app_open", "Open VS Code", "Safe", "ok", null, null, null))
            .Concat(Enumerable.Range(0, 3).Select(i => new ActivityEntry(100 + i, DateTimeOffset.Now.AddHours(-i), ActivityKinds.Request, null, "keep it short please", null, "ok", null, null, null)))
            .ToList();
        var patterns = PatternLearner.Analyze(entries, DateTimeOffset.Now);
        Assert.Contains(patterns, p => p.Key == "app_time:vs code:9" && p.Content == "Usually opens VS Code around 09:00." && p.Reason.Contains("5 times"));
        Assert.Contains(patterns, p => p.Key == "style:brief" && p.Kind == MemoryKinds.Preference);

        var learner = host.Get<PatternLearner>();
        // Learning is opt-in.
        host.Settings.Update(s => s.Memory.LearnPatterns = false);
        Assert.Equal(0, learner.Run(DateTimeOffset.Now).Proposed);
    }

    [Fact]
    public void Learner_proposes_once_and_respects_rejection()
    {
        using var host = new TestHost(s => s.Memory.LearnPatterns = true);
        var log = host.Get<ActivityLog>();
        for (var i = 0; i < 3; i++) log.Record(ActivityKinds.Request, "make it shorter next time", status: "ok");
        var learner = host.Get<PatternLearner>();
        var memories = host.Get<MemoryStore>();

        var first = learner.Run(DateTimeOffset.Now);
        Assert.Equal(1, first.Proposed);
        var learned = memories.List(confirmed: false).Single();
        Assert.Equal(MemorySources.Learned, learned.Source);
        Assert.Equal("learner", learned.Provenance!.Via);
        Assert.Contains("3 times", learned.Provenance.Reason);

        Assert.Equal(0, learner.Run(DateTimeOffset.Now).Proposed); // not proposed twice
        Assert.True(learner.Reject(learned.Id));
        Assert.Empty(memories.List());
        var again = learner.Run(DateTimeOffset.Now);
        Assert.Equal(0, again.Proposed);
        Assert.Equal(1, again.SkippedRejected);
    }

    [Fact]
    public void Confirmed_learned_items_become_facts()
    {
        using var host = new TestHost();
        var knowledge = host.Get<KnowledgeService>();
        var m = knowledge.Remember(new NewMemory("Prefers meetings after 11am", MemoryKinds.Preference, null, MemorySources.Learned, Provenance: new MemoryProvenance("learner", Reason: "test")));
        Assert.False(m.IsConfirmed);
        var c = knowledge.Memories.Confirm(m.Id)!;
        Assert.True(c.IsConfirmed);
        Assert.Equal(MemorySources.UserConfirmed, c.Source);
        Assert.Equal(1.0, c.Confidence);
        Assert.NotNull(c.ConfirmedAt);
        Assert.Equal("test", c.Provenance!.Reason); // the original evidence is kept
    }

    [Fact]
    public async Task Semantic_recall_finds_meaning_not_just_words()
    {
        var embedder = new ConceptEmbedder();
        using var host = new TestHost(s =>
        {
            s.Ai.Providers = [new ProviderConfig { Id = "emb", Name = "Embedder", Kind = "emb", IsLocal = true, Enabled = true }];
            s.Ai.EmbeddingModel = "concepts";
        });
        host.Get<ProviderRegistry>().Factory = _ => embedder;
        host.Settings.Update(_ => { });
        var knowledge = host.Get<KnowledgeService>();
        knowledge.Remember(new NewMemory("I drive a red car to work"));
        knowledge.Remember(new NewMemory("My sister lives in Alexandria"));
        await knowledge.BackfillAsync(default);

        // "automobile" shares no word with "car", but means the same thing.
        var hits = await knowledge.RecallAsync("which automobile do I have", 5, default);
        var top = hits.First();
        Assert.Contains("red car", top.Memory.Content);
        Assert.True(top.Semantic);
        var status = await knowledge.Semantic.StatusAsync(KnowledgeService.MemoryOwner, 2, default);
        Assert.True(status.Available);
        Assert.Equal(2, status.Indexed);
    }

    [Fact]
    public async Task Without_an_embedding_model_search_is_keyword_only_and_says_so()
    {
        using var host = new TestHost();
        var knowledge = host.Get<KnowledgeService>();
        knowledge.Remember(new NewMemory("I drive a red car to work"));
        Assert.Empty(await knowledge.RecallAsync("automobile", 5, default));
        Assert.Single(await knowledge.RecallAsync("car", 5, default));
        var status = await knowledge.Semantic.StatusAsync(KnowledgeService.MemoryOwner, 1, default);
        Assert.False(status.Available);
        Assert.Contains("Keyword search", status.Message);
    }

    [Fact]
    public void New_entities_and_aliases_pick_up_memories_that_already_mention_them()
    {
        using var host = new TestHost();
        var knowledge = host.Get<KnowledgeService>();
        var old = knowledge.Remember(new NewMemory("The CityCrep contract renews in March"));
        var arabic = knowledge.Remember(new NewMemory("اجتماع سيتي كريب يوم الحد"));
        var city = knowledge.Entities.Upsert(EntityTypes.Organization, "CityCrep");
        knowledge.LinkExisting(city);
        Assert.Contains(old.Id, knowledge.Entities.MemoryIdsOf(city.Id));
        Assert.DoesNotContain(arabic.Id, knowledge.Entities.MemoryIdsOf(city.Id));
        knowledge.LinkExisting(knowledge.Entities.Update(city.Id, aliases: ["سيتي كريب"])!);
        Assert.Contains(arabic.Id, knowledge.Entities.MemoryIdsOf(city.Id));
    }

    [Fact]
    public void Deleting_a_memory_removes_its_links_and_vectors()
    {
        using var host = new TestHost();
        var knowledge = host.Get<KnowledgeService>();
        var m = knowledge.Remember(new NewMemory("Sara manages Atlas", MemoryKinds.Person, "Sara"));
        var sara = knowledge.Entities.Find("Sara")!;
        Assert.Contains(m.Id, knowledge.Entities.MemoryIdsOf(sara.Id));
        knowledge.Memories.Delete(m.Id);
        Assert.Empty(knowledge.Entities.MemoryIdsOf(sara.Id));
    }

    /// <summary>Embeds by concept: synonyms land on the same dimension. Real models do this by learning.</summary>
    private sealed class ConceptEmbedder : IChatProvider, IModelCatalog, IEmbeddingProvider
    {
        private static readonly string[][] Concepts =
        [
            ["car", "automobile", "vehicle", "drive"],
            ["sister", "brother", "family"],
            ["alexandria", "cairo", "city"],
            ["work", "office", "job"],
        ];

        public string Id => "emb";
        public string Name => "Embedder";
        public bool IsLocal => true;
        public Task<ChatResponse> CompleteAsync(ChatRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProviderStatus> CheckAsync(CancellationToken ct) => Task.FromResult(new ProviderStatus(Id, false, "embeddings only", ["concepts"], DateTimeOffset.Now));
        public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<ModelInfo>>([new ModelInfo { Name = "concepts", Capabilities = [ModelCapabilities.Embedding], CapabilitiesReported = true }]);

        public Task<float[][]> EmbedAsync(string model, IReadOnlyList<string> inputs, CancellationToken ct) =>
            Task.FromResult(inputs.Select(text =>
            {
                var v = new float[Concepts.Length + 1];
                v[^1] = 0.05f;
                foreach (var w in text.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    for (var i = 0; i < Concepts.Length; i++)
                        if (Concepts[i].Contains(w.Trim('.', '?', ','))) v[i] += 1;
                return v;
            }).ToArray());
    }
}
