using System.Linq;
using FluxMux.Avalonia.Views;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class HelpTopicSearchTests
{
    [Fact]
    public void Empty_query_matches_any_topic()
    {
        Assert.True(HelpTopicSearch.Matches("llama-server install CUDA", ""));
        Assert.True(HelpTopicSearch.Matches("llama-server install CUDA", "   "));
        Assert.True(HelpTopicSearch.Matches("llama-server install CUDA", null));
    }

    [Fact]
    public void All_words_must_appear()
    {
        const string body = "Route requests can ask before loading a different local model.";
        Assert.True(HelpTopicSearch.Matches(body, "route local"));
        Assert.False(HelpTopicSearch.Matches(body, "route cloud gemini"));
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        Assert.True(HelpTopicSearch.Matches("NVIDIA CUDA zip", "cuda"));
    }

    [Fact]
    public void Sections_label_the_topics_beneath_them()
    {
        var display = HelpTopicSearch.GroupBySection([
            Topic("What AI-FluxMux does", string.Empty),
            Topic("Servers", "Setting up"),
            Topic("Model profiles", "Setting up"),
            Topic("Appendix A", "Appendices")
        ]);

        Assert.Equal(
            ["What AI-FluxMux does", "Setting up", "Servers", "Model profiles", "Appendices", "Appendix A"],
            display.Select(entry => entry.Title));
        Assert.True(display[1].IsSection);
        Assert.False(display[1].IsTopic);
        Assert.Null(display[1].Target);
        Assert.True(display[2].IsTopic);
    }

    [Fact]
    public void A_section_with_no_remaining_topics_is_dropped()
    {
        var display = HelpTopicSearch.GroupBySection([
            Topic("Servers", "Setting up")
        ]);

        Assert.Equal(["Setting up", "Servers"], display.Select(entry => entry.Title));
        Assert.DoesNotContain(display, entry => entry.Title == "Appendices");
    }

    [Fact]
    public void A_cross_reference_finds_the_topic_it_names()
    {
        var topics = Reference();

        Assert.Equal("DeepSeek Harness", HelpTopicSearch.FindByReference(topics, "DeepSeek Harness")?.Title);
        Assert.Equal("Cline", HelpTopicSearch.FindByReference(topics, "cline")?.Title);
        Assert.Equal("Cline", HelpTopicSearch.FindByReference(topics, " Cline ")?.Title);
    }

    [Fact]
    public void A_topic_id_still_finds_the_topic_after_the_title_changes()
    {
        var topics = new[]
        {
            new HelpTopicEntry
            {
                Title = "Forwarding rules",
                Id = "topic.port_rules",
                Section = "Setting up"
            }
        };

        Assert.Equal("Forwarding rules", HelpTopicSearch.FindByReference(topics, "topic.port_rules")?.Title);
        Assert.Equal("Forwarding rules", HelpTopicSearch.FindByReference(topics, "topic_port_rules")?.Title);
        Assert.Equal("Forwarding rules", HelpTopicSearch.FindByReference(topics, "Forwarding rules")?.Title);
    }

    [Fact]
    public void A_word_bookmark_still_finds_the_topic()
    {
        var topics = Reference();

        // Word writes a bookmark without spaces, and its own heading anchors lose the
        // punctuation as well.
        Assert.Equal("DeepSeek Harness", HelpTopicSearch.FindByReference(topics, "DeepSeek_Harness")?.Title);
        Assert.Equal(
            "Appendix C — Endpoint adapters (JSON)",
            HelpTopicSearch.FindByReference(topics, "AppendixCEndpointAdaptersJSON")?.Title);
    }

    [Fact]
    public void A_shortened_reference_finds_the_topic_it_starts()
    {
        Assert.Equal(
            "Appendix C — Endpoint adapters (JSON)",
            HelpTopicSearch.FindByReference(Reference(), "Appendix C")?.Title);
    }

    [Fact]
    public void A_reference_to_nothing_is_left_for_the_browser()
    {
        var topics = Reference();

        Assert.Null(HelpTopicSearch.FindByReference(topics, "Harness settings.yaml"));
        Assert.Null(HelpTopicSearch.FindByReference(topics, string.Empty));
        Assert.Null(HelpTopicSearch.FindByReference(topics, null));
    }

    [Fact]
    public void A_banner_is_not_a_link_target()
    {
        // Banners group the index and cannot be picked, so a link must not land on one.
        var topics = new[]
        {
            new HelpTopicEntry { Title = "Cloud models", Section = "Cloud models", IsSection = true },
            Topic("Cloud providers", "Cloud models")
        };

        Assert.Null(HelpTopicSearch.FindByReference(topics, "Cloud models"));
    }

    private static HelpTopicEntry[] Reference() =>
    [
        Topic("Cline", "Setting up"),
        Topic("DeepSeek Harness", "Setting up"),
        Topic("Appendix C — Endpoint adapters (JSON)", "Appendices")
    ];

    private static HelpTopicEntry Topic(string title, string section)
        => new() { Title = title, Section = section, SearchText = title };
}
