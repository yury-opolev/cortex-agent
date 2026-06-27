using System.Diagnostics;
using Cortex.Contained.Agent.Host.Memory;

namespace Cortex.Contained.Evals;

/// <summary>
/// Eval tests using synthesized persona data across four countries.
/// Each persona has three life aspects (career, dev environment / hobbies,
/// personal life) that are introduced across multiple conversation turns.
///
/// The tests verify that the extraction + consolidation pipeline:
/// <list type="bullet">
/// <item>Extracts distinct facts into separate memories.</item>
/// <item>Merges related facts about the same topic (no duplicates).</item>
/// <item>Updates or replaces stale facts when contradicting info arrives.</item>
/// <item>Handles multi-turn, cross-aspect conversations correctly.</item>
/// </list>
///
/// Run with: dotnet test tests/Cortex.Contained.Evals --filter "Category=PersonaMemory"
/// </summary>
[Trait("Category", "PersonaMemory")]
[Collection("Evals")]
public sealed class PersonaMemoryEvals
{
    private readonly EvalFixture _fixture;
    private readonly ITestOutputHelper _output;

    public PersonaMemoryEvals(EvalFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Canada — Priya Sharma (UX designer, rock climber, family life)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Canada: Extracts career facts for Priya")]
    public async Task Canada_Career()
    {
        await RunScenarioAsync("Canada: Career — Priya Sharma", async env =>
        {
            var u1 = "I'm Priya, I work as a senior UX designer at Shopify in Toronto. Been there about four years now.";
            var a1 = "That's great, Priya! Shopify is an amazing company. How are you enjoying the UX design work there?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");
            Assert.True(memories.Count <= 4, $"Expected at most 4 memories, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Priya", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Shopify", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("UX", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory to mention Priya, Shopify, or UX design");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Canada: Extracts hobbies for Priya")]
    public async Task Canada_Hobbies()
    {
        await RunScenarioAsync("Canada: Hobbies — Priya Sharma", async env =>
        {
            var u1 = "I'm really into rock climbing. I go to the Toronto Climbing Academy every weekend and I'm working towards leading 5.11s outdoors.";
            var a1 = "That's impressive! 5.11 is solid outdoor climbing. Do you have a favourite crag in Ontario?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("climb", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Toronto Climbing", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory to mention climbing");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Canada: Extracts personal life for Priya")]
    public async Task Canada_Personal()
    {
        await RunScenarioAsync("Canada: Personal life — Priya Sharma", async env =>
        {
            var u1 = "My partner Arjun and I just adopted a rescue dog named Koda. We live in a condo in Liberty Village.";
            var a1 = "Congratulations on adopting Koda! Liberty Village is a nice area. How is Koda settling in to condo life?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Koda", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Arjun", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Liberty Village", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory to mention Koda, Arjun, or Liberty Village");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Canada: Multi-turn cross-aspect dedup for Priya")]
    public async Task Canada_CrossAspectDedup()
    {
        await RunScenarioAsync("Canada: Cross-aspect dedup — Priya Sharma", async env =>
        {
            var inputs = new List<EvalExtractionInput>();

            // Turn 1: career
            var u1 = "I'm Priya, a UX designer at Shopify in Toronto.";
            var a1 = "Nice to meet you! How do you like working at Shopify?";
            await RunExtractionAsync(env, u1, a1);
            inputs.Add(Input(u1, a1));

            // Turn 2: hobby + mentions Toronto again
            var u2 = "After work I head to the climbing gym here in Toronto. Been climbing for three years.";
            var a2 = "Rock climbing is a great way to unwind after a design-heavy day. Indoor or outdoor?";
            await RunExtractionAsync(env, u2, a2);
            inputs.Add(Input(u2, a2));

            // Turn 3: personal + mentions Toronto again
            var u3 = "My partner Arjun and I live in Liberty Village. We have a dog named Koda.";
            var a3 = "Liberty Village is such a vibrant neighbourhood! Does Koda enjoy the parks there?";
            await RunExtractionAsync(env, u3, a3);
            inputs.Add(Input(u3, a3));

            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            // Should have extracted multiple distinct facts but NOT duplicated Toronto across every memory
            Assert.True(memories.Count >= 3,
                $"Expected at least 3 distinct memories (career, hobby, personal), got {memories.Count}");
            Assert.True(memories.Count <= 8,
                $"Expected at most 8 memories (no excessive duplication), got {memories.Count}");

            // Check each aspect is represented
            var all = JoinContent(memories);
            Assert.True(all.Contains("Shopify", StringComparison.OrdinalIgnoreCase) ||
                         all.Contains("UX", StringComparison.OrdinalIgnoreCase),
                "Expected career aspect (Shopify/UX) in memories");
            Assert.True(all.Contains("climb", StringComparison.OrdinalIgnoreCase),
                "Expected hobby aspect (climbing) in memories");
            Assert.True(all.Contains("Koda", StringComparison.OrdinalIgnoreCase) ||
                         all.Contains("Arjun", StringComparison.OrdinalIgnoreCase),
                "Expected personal aspect (Koda/Arjun) in memories");

            return (memories, inputs.ToArray());
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Argentina — Mateo Ruiz (data engineer, tango, family)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Argentina: Extracts career facts for Mateo")]
    public async Task Argentina_Career()
    {
        await RunScenarioAsync("Argentina: Career — Mateo Ruiz", async env =>
        {
            var u1 = "I'm Mateo, I work as a data engineer at MercadoLibre in Buenos Aires. We mostly use Spark and Airflow for our ETL pipelines.";
            var a1 = "MercadoLibre is huge in Latin America! Spark and Airflow are solid choices for data engineering. What scale are you dealing with?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Mateo", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("MercadoLibre", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("data engineer", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory to mention Mateo, MercadoLibre, or data engineering");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Argentina: Extracts dev environment for Mateo")]
    public async Task Argentina_DevEnvironment()
    {
        await RunScenarioAsync("Argentina: Dev environment — Mateo Ruiz", async env =>
        {
            var u1 = "My dev setup is a ThinkPad running Fedora with Neovim. I do everything in the terminal — tmux, lazygit, ripgrep. Can't stand IDEs.";
            var a1 = "A terminal-centric workflow! Neovim on Fedora is a great combo. Have you customized your config much?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Neovim", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Fedora", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("ThinkPad", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("terminal", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory about dev environment");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Argentina: Extracts personal life for Mateo")]
    public async Task Argentina_Personal()
    {
        await RunScenarioAsync("Argentina: Personal life — Mateo Ruiz", async env =>
        {
            var u1 = "My wife Luciana and I dance tango every Friday in San Telmo. Our daughter Sofia just turned two — she tries to dance with us, it's hilarious.";
            var a1 = "That's wonderful! San Telmo is the heart of tango culture. Sofia sounds adorable trying to join in!";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Luciana", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("tango", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Sofia", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory about family or tango");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Argentina: Career update replaces stale info for Mateo")]
    public async Task Argentina_CareerUpdate()
    {
        await RunScenarioAsync("Argentina: Career update — Mateo Ruiz", async env =>
        {
            // Seed: works at MercadoLibre
            await env.SeedMemoryAsync(
                "Mateo works as a data engineer at MercadoLibre in Buenos Aires using Spark and Airflow",
                "Career info",
                ["career"]);

            // Update: switched jobs
            var u1 = "Big news — I left MercadoLibre last month. I'm now at Globant as a senior data architect. Much bigger scope.";
            var a1 = "Congratulations on the move to Globant! Senior data architect sounds like a great step up. What's the new role like?";
            await RunExtractionAsync(env, u1, a1);

            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            // Should NOT have a standalone "works at MercadoLibre" memory anymore
            var staleMemories = memories.Where(m =>
                m.Content.Contains("MercadoLibre", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("Globant", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("left", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(staleMemories.Count == 0,
                $"Expected stale MercadoLibre-only memory to be updated, found: " +
                string.Join(" | ", staleMemories.Select(m => m.Content)));

            var globantMemories = memories.Where(m =>
                m.Content.Contains("Globant", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(globantMemories.Count >= 1,
                "Expected at least one memory mentioning Globant");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  China — Wei Zhang (mobile dev, photography, personal)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "China: Extracts career facts for Wei")]
    public async Task China_Career()
    {
        await RunScenarioAsync("China: Career — Wei Zhang", async env =>
        {
            var u1 = "I'm Wei, I'm a mobile developer at ByteDance in Beijing. I work on the Douyin iOS team — we use Swift and a custom MVVM architecture.";
            var a1 = "Working on Douyin at ByteDance must be intense! How large is the iOS team?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Wei", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("ByteDance", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Douyin", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Swift", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory to mention Wei, ByteDance, Douyin, or Swift");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "China: Extracts hobbies for Wei")]
    public async Task China_Hobbies()
    {
        await RunScenarioAsync("China: Hobbies — Wei Zhang", async env =>
        {
            var u1 = "Photography is my biggest hobby. I shoot with a Fujifilm X-T5 and I love street photography around the hutongs. I also post on Xiaohongshu.";
            var a1 = "The X-T5 is a fantastic camera for street photography! The hutongs must give you amazing subjects. Do you have a favourite area?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Fujifilm", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("photography", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("hutong", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory about photography");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "China: Extracts personal life for Wei")]
    public async Task China_Personal()
    {
        await RunScenarioAsync("China: Personal life — Wei Zhang", async env =>
        {
            var u1 = "My girlfriend Mei and I adopted a cat called Boba last month. We also play badminton together every weekend at the community court. I'm training for a doubles tournament in April.";
            var a1 = "A cat and badminton — sounds like you two have a great balance! Is Boba adjusting well?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Mei", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Boba", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("badminton", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory about personal life (girlfriend, cat, or badminton)");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "China: Multi-turn all aspects for Wei, no duplication")]
    public async Task China_MultiTurnNoDuplication()
    {
        await RunScenarioAsync("China: Multi-turn all aspects — Wei Zhang", async env =>
        {
            var inputs = new List<EvalExtractionInput>();

            // Turn 1: career
            var u1 = "I'm Wei, mobile developer at ByteDance in Beijing, working on Douyin with Swift.";
            var a1 = "ByteDance is a big company! What's the team culture like?";
            await RunExtractionAsync(env, u1, a1);
            inputs.Add(Input(u1, a1));

            // Turn 2: hobby
            var u2 = "In my free time I do street photography with my Fujifilm X-T5, mostly around the old hutongs in Beijing.";
            var a2 = "The contrast between old hutongs and modern Beijing must make for stunning shots!";
            await RunExtractionAsync(env, u2, a2);
            inputs.Add(Input(u2, a2));

            // Turn 3: personal (deliberately distinct from career/hobby topics)
            var u3 = "My girlfriend Mei and I adopted a cat called Boba last month. We play badminton together every weekend — training for a doubles tournament in April.";
            var a3 = "That's lovely! Is Boba adjusting well? And good luck with the tournament!";
            await RunExtractionAsync(env, u3, a3);
            inputs.Add(Input(u3, a3));

            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 3,
                $"Expected at least 3 memories (career, hobby, personal), got {memories.Count}");
            Assert.True(memories.Count <= 8,
                $"Expected at most 8 memories, got {memories.Count}");

            return (memories, inputs.ToArray());
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Australia — Mia Thompson (DevOps, surfing, personal)
    // ═══════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "Australia: Extracts career facts for Mia")]
    public async Task Australia_Career()
    {
        await RunScenarioAsync("Australia: Career — Mia Thompson", async env =>
        {
            var u1 = "I'm Mia, I'm a DevOps engineer at Atlassian in Sydney. I manage our Kubernetes clusters and CI/CD pipelines with ArgoCD and GitHub Actions.";
            var a1 = "Atlassian is a fantastic place for DevOps! ArgoCD with GitHub Actions is a solid GitOps stack. How many clusters are you managing?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Mia", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Atlassian", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("DevOps", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Kubernetes", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory to mention Mia, Atlassian, DevOps, or Kubernetes");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Australia: Extracts hobbies for Mia")]
    public async Task Australia_Hobbies()
    {
        await RunScenarioAsync("Australia: Hobbies — Mia Thompson", async env =>
        {
            var u1 = "I surf almost every morning before work at Bondi Beach. I've been surfing since I was twelve. I also play in a local women's AFL team on Saturdays.";
            var a1 = "Dawn patrol at Bondi — that's living the dream! AFL and surfing is a very Aussie combo. What position do you play?";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("surf", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Bondi", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("AFL", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory about surfing or AFL");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Australia: Extracts personal life for Mia")]
    public async Task Australia_Personal()
    {
        await RunScenarioAsync("Australia: Personal life — Mia Thompson", async env =>
        {
            var u1 = "My girlfriend Emma and I just moved into a flat in Surry Hills. We have two cats, Pixel and Byte. Very on-brand for tech people.";
            var a1 = "Surry Hills is a great neighbourhood! Pixel and Byte — those are perfect names for a DevOps engineer's cats.";

            await RunExtractionAsync(env, u1, a1);
            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.True(memories.Count >= 1, $"Expected at least 1 memory, got {memories.Count}");

            var all = JoinContent(memories);
            Assert.True(
                all.Contains("Emma", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Surry Hills", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Pixel", StringComparison.OrdinalIgnoreCase) ||
                all.Contains("Byte", StringComparison.OrdinalIgnoreCase),
                "Expected at least one memory about Emma, Surry Hills, or the cats");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    [Fact(DisplayName = "Australia: Hobby update replaces stale info for Mia")]
    public async Task Australia_HobbyUpdate()
    {
        await RunScenarioAsync("Australia: Hobby update — Mia Thompson", async env =>
        {
            // Seed: surfs at Bondi
            await env.SeedMemoryAsync(
                "Mia surfs almost every morning at Bondi Beach in Sydney before work",
                "Hobby — surfing",
                ["hobbies"]);

            // Update: switched beaches
            var u1 = "I actually stopped surfing at Bondi — too crowded these days. I now drive out to Maroubra every morning. Way better waves and fewer people.";
            var a1 = "Maroubra is a local's favourite! Less touristy and more consistent swell. Worth the extra drive?";
            await RunExtractionAsync(env, u1, a1);

            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            // Should NOT have a standalone "surfs at Bondi" memory anymore
            var staleMemories = memories.Where(m =>
                m.Content.Contains("Bondi", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("Maroubra", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("stopped", StringComparison.OrdinalIgnoreCase) &&
                !m.Content.Contains("switched", StringComparison.OrdinalIgnoreCase)).ToList();

            Assert.True(staleMemories.Count == 0,
                $"Expected stale Bondi-only surfing memory to be updated, found: " +
                string.Join(" | ", staleMemories.Select(m => m.Content)));

            var maroubraMemories = memories.Where(m =>
                m.Content.Contains("Maroubra", StringComparison.OrdinalIgnoreCase)).ToList();
            Assert.True(maroubraMemories.Count >= 1,
                "Expected at least one memory mentioning Maroubra");

            return (memories, new[] { Input(u1, a1) });
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Cross-persona: all four seeded, then queried
    // ═══════════════════════════════════════════════════════════════════════

    [Fact(DisplayName = "All personas: Seeded facts are searchable by content")]
    public async Task AllPersonas_SeededFactsSearchable()
    {
        await RunScenarioAsync("All personas: Seeded facts searchable", async env =>
        {
            // Seed one fact per persona
            await env.SeedMemoryAsync("Priya Sharma is a senior UX designer at Shopify in Toronto, Canada", "Priya — career");
            await env.SeedMemoryAsync("Mateo Ruiz is a data engineer at MercadoLibre in Buenos Aires, Argentina, using Spark and Airflow", "Mateo — career");
            await env.SeedMemoryAsync("Wei Zhang is a mobile developer at ByteDance in Beijing, China, working on Douyin with Swift", "Wei — career");
            await env.SeedMemoryAsync("Mia Thompson is a DevOps engineer at Atlassian in Sydney, Australia, managing Kubernetes clusters", "Mia — career");

            var memories = await env.GetAllMemoriesAsync();
            LogMemories(memories);

            Assert.Equal(4, memories.Count);

            // Search for each persona
            var priyaResults = await env.MemoryService.SearchAsync("UX designer Toronto", 3, 0.3f);
            Assert.True(priyaResults.Count >= 1, "Expected to find Priya via search");
            Assert.Contains(priyaResults, r => r.Content.Contains("Priya", StringComparison.OrdinalIgnoreCase));

            var mateoResults = await env.MemoryService.SearchAsync("data engineer Buenos Aires", 3, 0.3f);
            Assert.True(mateoResults.Count >= 1, "Expected to find Mateo via search");
            Assert.Contains(mateoResults, r => r.Content.Contains("Mateo", StringComparison.OrdinalIgnoreCase));

            var weiResults = await env.MemoryService.SearchAsync("mobile developer Beijing Douyin", 3, 0.3f);
            Assert.True(weiResults.Count >= 1, "Expected to find Wei via search");
            Assert.Contains(weiResults, r => r.Content.Contains("Wei", StringComparison.OrdinalIgnoreCase));

            var miaResults = await env.MemoryService.SearchAsync("DevOps Kubernetes Sydney", 3, 0.3f);
            Assert.True(miaResults.Count >= 1, "Expected to find Mia via search");
            Assert.Contains(miaResults, r => r.Content.Contains("Mia", StringComparison.OrdinalIgnoreCase));

            // No extraction inputs — all seeded
            return (memories, Array.Empty<EvalExtractionInput>());
        });
    }

    [Fact(DisplayName = "All personas: No trivial extraction from small talk")]
    public async Task AllPersonas_TrivialConversationNoExtraction()
    {
        await RunScenarioAsync("All personas: Trivial conversation — no extraction", async env =>
        {
            // Seed some context so the store isn't empty
            await env.SeedMemoryAsync("Priya Sharma is a UX designer at Shopify in Toronto", "Priya — career");
            await env.SeedMemoryAsync("Mateo Ruiz is a data engineer at MercadoLibre in Buenos Aires", "Mateo — career");

            var before = await env.GetAllMemoriesAsync();

            // Trivial small talk — should NOT create new memories
            var u1 = "Good morning! How's the weather today?";
            var a1 = "Good morning! I'm an AI so I don't experience weather, but I hope it's nice where you are!";
            await RunExtractionAsync(env, u1, a1);

            var after = await env.GetAllMemoriesAsync();
            LogMemories(after);

            Assert.Equal(before.Count, after.Count);

            return (after, new[] { Input(u1, a1) });
        });
    }

    // ═══════════════════════════════════════════════════════════════════════
    //  Infrastructure (same patterns as MemoryConsolidationEvals)
    // ═══════════════════════════════════════════════════════════════════════

    private static EvalExtractionInput Input(string user, string assistant) =>
        new() { UserMessage = user, AssistantResponse = assistant };

    private static string JoinContent(List<(string MemoryId, string Content)> memories) =>
        string.Join(" ", memories.Select(m => m.Content));

    private void LogMemories(List<(string MemoryId, string Content)> memories)
    {
        _output.WriteLine($"Extracted {memories.Count} memories:");
        foreach (var (id, content) in memories)
        {
            _output.WriteLine($"  [{id[..8]}] {content}");
        }
    }

    /// <summary>
    /// Wraps a scenario in structured recording: captures LLM calls, timing,
    /// final memory state, and pass/fail status into the <see cref="EvalRecorder"/>.
    /// </summary>
    private async Task RunScenarioAsync(
        string scenarioName,
        Func<EvalMemoryEnv, Task<(List<(string MemoryId, string Content)> Memories, EvalExtractionInput[] Inputs)>> scenarioAction)
    {
        using var env = _fixture.CreateMemoryEnv();

        _fixture.RecordingClient.Clear();

        var sw = Stopwatch.StartNew();
        string? failureMessage = null;
        List<(string MemoryId, string Content)> finalMemories = [];
        EvalExtractionInput[] inputs = [];

        try
        {
            (finalMemories, inputs) = await scenarioAction(env).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            failureMessage = ex.Message;
            try
            {
                finalMemories = await env.GetAllMemoriesAsync().ConfigureAwait(false);
            }
#pragma warning disable CA1031 // Best-effort memory capture on failure
            catch
            {
                // Ignore — already in a failure path
            }
#pragma warning restore CA1031

            throw;
        }
        finally
        {
            await env.StopExtractionServiceAsync().ConfigureAwait(false);

            sw.Stop();

            var result = new EvalScenarioResult
            {
                Name = scenarioName,
                Passed = failureMessage is null,
                FailureMessage = failureMessage,
                DurationMs = sw.Elapsed.TotalMilliseconds,
                MemoryCount = finalMemories.Count,
                FinalMemories = finalMemories.Select(m => m.Content).ToList(),
                LlmCalls = [.. _fixture.RecordingClient.GetCalls()],
                ExtractionInputs = [.. inputs],
            };

            _fixture.Recorder.RecordScenario(result);

            _output.WriteLine($"--- Scenario: {scenarioName} ---");
            _output.WriteLine($"  Duration: {sw.Elapsed.TotalSeconds:F1}s");
            _output.WriteLine($"  LLM calls: {result.LlmCalls.Count}");
            _output.WriteLine($"  Final memories: {finalMemories.Count}");
            if (failureMessage is not null)
            {
                _output.WriteLine($"  FAILED: {failureMessage}");
            }
        }
    }

    /// <summary>
    /// Runs the full extraction pipeline (enqueue + wait for processing).
    /// </summary>
    private async Task RunExtractionAsync(EvalMemoryEnv env, string userMessage, string assistantResponse)
    {
        if (!env.ExtractionServiceStarted)
        {
            await env.ExtractionService.StartAsync(env.ServiceCts.Token).ConfigureAwait(false);
            env.ExtractionServiceStarted = true;
        }

        var enqueued = env.ExtractionService.EnqueueBatch(
            [
                new Cortex.Contained.Agent.Host.Agent.ExtractionEntry { Role = "user", Content = userMessage, Timestamp = DateTimeOffset.UtcNow },
                new Cortex.Contained.Agent.Host.Agent.ExtractionEntry { Role = "assistant", Content = assistantResponse, Timestamp = DateTimeOffset.UtcNow },
            ], _fixture.Model, "eval-persona");
        Assert.True(enqueued, "Failed to enqueue message pair for extraction");

        await env.ExtractionService.WaitForIdleAsync(TimeSpan.FromSeconds(60)).ConfigureAwait(false);
    }
}
