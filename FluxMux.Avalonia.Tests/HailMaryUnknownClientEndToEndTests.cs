using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;
using Xunit.Abstractions;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// A chat from a Client app AI-FluxMux knows nothing about keeps running with no Hail Mary at all
/// (the 95 % last resort shortens it), run through the real gateway over
/// HTTP: a plain OpenAI-compatible client (unrecognised system prompt, its own tool names) whose
/// chat no longer fits the loaded Context. A fake llama-server behind the daemon port counts
/// tokens (chars / 4) and records exactly what it was sent, so the test checks what the model
/// would really have received, not what a helper returned.
/// </summary>
public sealed class HailMaryUnknownClientEndToEndTests
{
    private const int Context = 8192;
    private readonly ITestOutputHelper _out;

    public HailMaryUnknownClientEndToEndTests(ITestOutputHelper output) => _out = output;

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static int Tokens(JsonNode? messages) => (messages?.ToJsonString().Length ?? 0) / 4;

    /// <summary>Answers input_tokens and chat completions; keeps every completion request.</summary>
    private sealed class FakeLlamaServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _stop = new();
        public ConcurrentQueue<JsonObject> Completions { get; } = new();
        public int Port { get; } = FreePort();

        public FakeLlamaServer()
        {
            _listener.Prefixes.Add("http://127.0.0.1:" + Port + "/");
            _listener.Start();
            _ = Task.Run(LoopAsync);
        }

        private async Task LoopAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch
                {
                    return;
                }

                try
                {
                    var path = ctx.Request.Url!.AbsolutePath;
                    var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync().ConfigureAwait(false);
                    var json = body.Length > 0 ? JsonNode.Parse(body) as JsonObject : null;
                    string reply;
                    var contentType = "application/json";
                    if (path.EndsWith("/input_tokens", StringComparison.Ordinal))
                    {
                        reply = new JsonObject { ["input_tokens"] = Tokens(json?["messages"]) }.ToJsonString();
                    }
                    else if (path.EndsWith("/chat/completions", StringComparison.Ordinal) && json is not null)
                    {
                        Completions.Enqueue(json);
                        if (json["stream"]?.GetValue<bool>() == true)
                        {
                            contentType = "text/event-stream";
                            var chunk = new JsonObject
                            {
                                ["id"] = "c", ["object"] = "chat.completion.chunk", ["model"] = "local",
                                ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["delta"] = new JsonObject { ["content"] = "Picking up where we left off." }, ["finish_reason"] = "stop" } },
                            };
                            reply = "data: " + chunk.ToJsonString() + "\n\ndata: [DONE]\n\n";
                        }
                        else
                        {
                            reply = new JsonObject
                            {
                                ["id"] = "c", ["object"] = "chat.completion", ["model"] = "local",
                                ["choices"] = new JsonArray { new JsonObject { ["index"] = 0, ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = "Picking up where we left off." }, ["finish_reason"] = "stop" } },
                                ["usage"] = new JsonObject { ["prompt_tokens"] = Tokens(json["messages"]), ["completion_tokens"] = 8 },
                            }.ToJsonString();
                        }
                    }
                    else
                    {
                        reply = "{\"status\":\"ok\"}";
                    }

                    var bytes = Encoding.UTF8.GetBytes(reply);
                    ctx.Response.ContentType = contentType;
                    ctx.Response.ContentLength64 = bytes.Length;
                    await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                    ctx.Response.Close();
                }
                catch
                {
                    try { ctx.Response.Abort(); } catch { }
                }
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
        }
    }

    /// <summary>A long chat from an OpenAI-compatible client AI-FluxMux does not recognise.</summary>
    private static JsonObject UnknownClientChat(int turns, bool stream)
    {
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = "You are Acme Pilot, a coding assistant. Use the tools to work in the user's project." },
            new JsonObject { ["role"] = "user", ["content"] = "Build a small dungeon game in game.js: rooms, a player, and a goblin to fight." },
        };
        for (var i = 0; i < turns; i++)
        {
            var id = "call_" + i;
            messages.Add(new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = "",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = id, ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = "acme_patch", ["arguments"] = new JsonObject { ["file"] = "game.js", ["hunk"] = "room " + i + " " + new string('x', 300) }.ToJsonString() },
                    },
                },
            });
            messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = id, ["content"] = "patched room " + i + ": " + new string('y', 400) });
        }

        messages.Add(new JsonObject { ["role"] = "user", ["content"] = "Now add a health bar for the goblin." });
        return new JsonObject
        {
            ["model"] = "local",
            ["stream"] = stream,
            ["messages"] = messages,
            ["tools"] = new JsonArray
            {
                new JsonObject { ["type"] = "function", ["function"] = new JsonObject { ["name"] = "acme_patch", ["parameters"] = new JsonObject { ["type"] = "object" } } },
            },
        };
    }

    // What this showed (25 Nov): for an unrecognised client whose profile leaves shortening to the
    // Client app, a chat ~2.4x the Context is shortened by the 95 % last resort on the FIRST turn,
    // so the client never sees a failure and an armed Hail Mary trim then has nothing left to cut
    // (logged as a no-op, and cleared). Hail Mary is still needed when the Client app itself stops
    // sending (frozen chat; only a RescueAdapter reaches it) or one turn alone is too large.
    // Note: the compaction keeps the newest user task, not the opening brief; in the app the
    // session note (VIBE_STATE, appended to the newest message) carries the task, which this
    // fake setup does not run.
    [Fact]
    public async Task An_unrecognised_client_chat_that_no_longer_fits_keeps_running()
    {
        var dir = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), "fluxmux-hm-e2e-" + Guid.NewGuid().ToString("N"))).FullName;
        using var llama = new FakeLlamaServer();
        var gatewayPort = FreePort();
        File.WriteAllText(Path.Combine(dir, "state.json"), new JsonObject
        {
            ["provider"] = "Local", ["mode"] = "local", ["phase"] = "ready",
            ["endpoint"] = "http://127.0.0.1:" + llama.Port + "/v1",
            ["local_port"] = llama.Port, ["local_phase"] = "ready",
            ["local_preferred_model"] = "test.gguf", ["local_variant"] = "(defaults)",
            ["local_context"] = Context, ["local_max_tokens"] = 1024,
            ["local_gpu_offload"] = "GPU only", ["routing_enabled"] = false,
            ["endpoint_app"] = "Other",
        }.ToJsonString());

        var host = new FluxMuxGatewayHost(
            Path.Combine(dir, "state.json"), Path.Combine(dir, "served.json"), Path.Combine(dir, "rec.json"),
            Path.Combine(dir, "breaker.json"), Path.Combine(dir, "reload.json"), Path.Combine(dir, "log.txt"),
            Path.Combine(dir, "ctx.json"), Path.Combine(dir, "route.json"));
        // The Client app shortens its own chat (as most agent clients do): AI-FluxMux keeps only
        // its 95 % last resort, so this is the case Hail Mary is for.
        host.GetLocalAutoCompactSettings = (_, _) => ("none", 0.80, 6, 8, 2000);
        host.GetUserClientReinflatesContext = () => true;

        using var cts = new CancellationTokenSource();
        await host.StartAsync(gatewayPort, cts.Token);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            async Task<(int Status, string Body)> Send(JsonObject chat)
            {
                using var response = await http.PostAsync(
                    "http://127.0.0.1:" + gatewayPort + "/v1/chat/completions",
                    new StringContent(chat.ToJsonString(), Encoding.UTF8, "application/json"));
                return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
            }

            // Far over the Context: what the client sends is ~3x what fits.
            var chat = UnknownClientChat(turns: 80, stream: false);
            _out.WriteLine("client sends ~" + Tokens(chat["messages"]) + " tokens to a " + Context + "-token Context");

            var first = await Send(chat);
            _out.WriteLine("turn 1: HTTP " + first.Status + ", llama-server asked " + llama.Completions.Count + " time(s); " + Trim(first.Body));
            Assert.Equal(200, first.Status);
            Assert.True(Tokens(llama.Completions.Last()["messages"]) < Context, "turn 1 was forwarded over the Context");

            var sent = llama.Completions.Last();
            var messages = (JsonArray)sent["messages"]!;
            var sentTokens = Tokens(messages);
            _out.WriteLine("llama-server received " + messages.Count + " messages, ~" + sentTokens + " tokens");
            Assert.True(sentTokens < Context, "the trimmed request still does not fit: " + sentTokens);
            await Task.Delay(300);
            foreach (var line in File.ReadAllLines(Path.Combine(dir, "log.txt")).Where(l => l.Contains("hail", StringComparison.OrdinalIgnoreCase) || l.Contains("compact", StringComparison.OrdinalIgnoreCase) || l.Contains("last resort", StringComparison.OrdinalIgnoreCase)))
            {
                _out.WriteLine("log: " + line);
            }

            foreach (var (m, i) in llama.Completions.Select((c, i) => (c, i)))
            {
                var first2 = ((JsonArray)m["messages"]!).Take(3).Select(x => x!["role"] + ": " + Trim(x["content"]?.ToJsonString() ?? ""));
                _out.WriteLine("request " + i + " starts: " + string.Join(" | ", first2));
            }

            var text = messages.ToJsonString();
            Assert.Contains("Now add a health bar for the goblin.", text);
            Assert.Contains("Picking up where we left off.", first.Body);

            // Every tool call that was kept still has its result, so no client or model rejects it.
            var calls = messages.OfType<JsonObject>().SelectMany(m => (m["tool_calls"] as JsonArray)?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
                .Select(c => c["id"]!.ToString()).ToHashSet();
            var results = messages.OfType<JsonObject>().Where(m => m["role"]?.ToString() == "tool").Select(m => m["tool_call_id"]!.ToString()).ToHashSet();
            Assert.True(calls.SetEquals(results), "a kept tool call lost its result, or a result lost its call");

            // The next turn from the same still-growing chat keeps working.
            var third = await Send(UnknownClientChat(turns: 82, stream: false));
            _out.WriteLine("turn 2 (chat grew): HTTP " + third.Status + ", sent ~" + Tokens(llama.Completions.Last()["messages"]) + " tokens; " + Trim(third.Body));
            Assert.Equal(200, third.Status);
            Assert.True(Tokens(llama.Completions.Last()["messages"]) < Context);
        }
        finally
        {
            cts.Cancel();
            await host.StopAsync();
            try { Directory.Delete(dir, recursive: true); } catch { }
        }

        static string Trim(string s) => s.Length > 300 ? s[..300] + "…" : s;
    }
}
