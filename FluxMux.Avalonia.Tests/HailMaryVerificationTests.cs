using System;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using FluxMux.Avalonia.Services.HailMary;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// After Hail Mary rebuilds a chat, the Client app's next request for that chat tells whether it
/// took the rebuild (27 Nov): nothing on screen shows it, and Cline silently resends the whole
/// chat when it did not. Works the same for any Client app Hail Mary rebuilds in place.
/// </summary>
[Collection("HailMaryVerification")]
public sealed class HailMaryVerificationTests
{
    private const string Opening = "Task: tidy the billing service and get the release ready. Codename KESTREL-5821.";

    private static JsonArray FullChat(int extra = 40)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are Cline, an AI coding agent." },
            new JsonObject { ["role"] = "user", ["content"] = Opening },
        };
        for (var i = 0; i < extra; i++)
        {
            messages.Add(new JsonObject { ["role"] = i % 2 == 0 ? "assistant" : "user", ["content"] = "Step " + i });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "retry" });
        return messages;
    }

    private static JsonArray RebuiltChat()
        => new()
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are Cline, an AI coding agent." },
            new JsonObject
            {
                ["role"] = "user",
                ["content"] = "Context summary:\n\n" + HandoverBrief.RescueSummaryHeading + " to make room.\n\n## Where it started\n" + Opening,
            },
            new JsonObject { ["role"] = "assistant", ["content"] = "Step 39" },
            new JsonObject { ["role"] = "user", ["content"] = "retry" },
        };

    [Fact]
    public void The_rebuilt_chat_is_reported_as_taken_and_the_check_ends()
    {
        HailMaryVerification.Reset();
        HailMaryVerification.Arm(ClientAppDetector.Cline, HandoverBrief.OpeningProbe(FullChat()), 43);
        var sentence = HailMaryVerification.Observe(RebuiltChat(), ClientAppDetector.Cline);
        Assert.NotNull(sentence);
        Assert.Contains("is using the rebuilt chat", sentence, StringComparison.Ordinal);
        Assert.False(HailMaryVerification.IsArmed);
    }

    [Fact]
    public void The_full_chat_again_is_reported_once_and_a_later_reopen_is_still_confirmed()
    {
        HailMaryVerification.Reset();
        HailMaryVerification.Arm(ClientAppDetector.Cline, HandoverBrief.OpeningProbe(FullChat()), 43);
        var first = HailMaryVerification.Observe(FullChat(), ClientAppDetector.Cline);
        Assert.Contains("sent the whole chat again", first, StringComparison.Ordinal);
        Assert.Contains("reopen this one from history", first, StringComparison.Ordinal);
        Assert.Null(HailMaryVerification.Observe(FullChat(), ClientAppDetector.Cline));
        Assert.True(HailMaryVerification.IsArmed);
        Assert.Contains("is using the rebuilt chat", HailMaryVerification.Observe(RebuiltChat(), ClientAppDetector.Cline), StringComparison.Ordinal);
    }

    [Fact]
    public void Another_chat_or_another_Client_app_is_ignored()
    {
        HailMaryVerification.Reset();
        HailMaryVerification.Arm(ClientAppDetector.Cline, HandoverBrief.OpeningProbe(FullChat()), 43);
        var other = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are Cline, an AI coding agent." },
            new JsonObject { ["role"] = "user", ["content"] = "A different task entirely." },
        };
        Assert.Null(HailMaryVerification.Observe(other, ClientAppDetector.Cline));
        Assert.Null(HailMaryVerification.Observe(FullChat(), ClientAppDetector.Pi));
        Assert.True(HailMaryVerification.IsArmed);
    }

    [Fact]
    public void The_check_expires()
    {
        HailMaryVerification.Reset();
        var armed = new DateTime(2026, 11, 27, 9, 0, 0, DateTimeKind.Utc);
        HailMaryVerification.Arm(ClientAppDetector.Cline, HandoverBrief.OpeningProbe(FullChat()), 43, armed);
        Assert.Null(HailMaryVerification.Observe(RebuiltChat(), ClientAppDetector.Cline, armed + HailMaryVerification.ArmedFor + TimeSpan.FromMinutes(1)));
        Assert.False(HailMaryVerification.IsArmed);
    }

    [Fact]
    public void It_works_the_same_for_Harness_with_Harness_advice()
    {
        HailMaryVerification.Reset();
        HailMaryVerification.Arm(ClientAppDetector.Harness, HandoverBrief.OpeningProbe(FullChat()), 43);
        var notTaken = HailMaryVerification.Observe(FullChat(), ClientAppDetector.Harness);
        Assert.Contains("Close and reopen this chat in " + ClientAppDetector.Harness, notTaken, StringComparison.Ordinal);
        Assert.DoesNotContain("Cline", notTaken, StringComparison.Ordinal);
        Assert.Contains("is using the rebuilt chat", HailMaryVerification.Observe(RebuiltChat(), ClientAppDetector.Harness), StringComparison.Ordinal);
    }
}

[CollectionDefinition("HailMaryVerification", DisableParallelization = true)]
public sealed class HailMaryVerificationCollection;
