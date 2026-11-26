using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// One saved chat per Client app (26 Nov): with pi and Cline both on Port, a failed Cline chat must
/// not be lost when pi sends something, and Hail Mary and the brief act on the failed one.
/// </summary>
[Collection("SavedChats")]
public sealed class SavedChatsTests
{
    private static SavedChat Chat(string? app, int minutesAgo, bool failed = false, int failedMinutesAgo = 0)
        => new(SavedChats.KeyFor(app), app, DateTime.UtcNow.AddMinutes(-minutesAgo), 0, failed,
            failed ? DateTime.UtcNow.AddMinutes(-failedMinutesAgo) : null, new JsonArray());

    [Fact]
    public void The_most_recently_failed_chat_is_preferred_over_a_newer_healthy_one()
    {
        var cline = Chat("Cline", 20, failed: true, failedMinutesAgo: 19);
        var pi = Chat("pi", 2);
        Assert.Equal("Cline", SavedChats.PickPreferred([cline, pi])!.ClientApp);
    }

    [Fact]
    public void With_no_failed_chat_the_newest_is_preferred()
        => Assert.Equal("pi", SavedChats.PickPreferred([Chat("Cline", 20), Chat("pi", 2)])!.ClientApp);

    [Fact]
    public void Labels_name_the_Client_app_and_say_failed()
    {
        Assert.StartsWith("Cline chat, ", Chat("Cline", 1, failed: true).Label, StringComparison.Ordinal);
        Assert.EndsWith(" (failed)", Chat("Cline", 1, failed: true).Label, StringComparison.Ordinal);
        Assert.StartsWith("Unrecognised Client app chat, ", Chat(null, 1).Label, StringComparison.Ordinal);
    }

    [Fact]
    public void Each_Client_app_keeps_its_own_chat_and_the_overflow_mark_follows_the_request()
    {
        var folder = Path.Combine(Path.GetTempPath(), "fluxmux-saved-chats-" + Guid.NewGuid().ToString("N"));
        try
        {
            SavedChats.Attach(folder);
            var clineChat = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "Cline task" } };
            var piChat = new JsonArray { new JsonObject { ["role"] = "user", ["content"] = "pi task" } };

            SavedChats.Keep(clineChat, "Cline");
            SavedChats.MarkCurrentOverflowed();
            SavedChats.Keep(piChat, "pi");

            var all = SavedChats.All();
            Assert.Equal(2, all.Count);
            Assert.True(SavedChats.Get("Cline")!.Overflowed);
            Assert.False(SavedChats.Get("pi")!.Overflowed);
            Assert.Equal("Cline", SavedChats.Preferred()!.ClientApp);

            // Cline sending again clears its failed mark.
            SavedChats.Keep(clineChat, "Cline");
            Assert.False(SavedChats.Get("Cline")!.Overflowed);
        }
        finally
        {
            SavedChats.Attach(Path.Combine(Path.GetTempPath(), "fluxmux-saved-chats-none"));
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch
            {
            }
        }
    }
}

/// <summary>
/// The saved chats are one static store that every gateway a test starts attaches to, so tests
/// that change it must not run alongside gateway tests.
/// </summary>
[CollectionDefinition("SavedChats", DisableParallelization = true)]
public sealed class SavedChatsCollection
{
}
