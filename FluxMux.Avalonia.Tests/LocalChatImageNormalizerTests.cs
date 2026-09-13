using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class LocalChatImageNormalizerTests
{
    private static readonly byte[] OnePixelPng =
    [
        0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x00, 0x00, 0x0D,
        0x49, 0x48, 0x44, 0x52, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01,
        0x08, 0x06, 0x00, 0x00, 0x00, 0x1F, 0x15, 0xC4, 0x89, 0x00, 0x00, 0x00,
        0x0A, 0x49, 0x44, 0x41, 0x54, 0x78, 0x9C, 0x63, 0x00, 0x01, 0x00, 0x00,
        0x05, 0x00, 0x01, 0x0D, 0x0A, 0x2D, 0xB4, 0x00, 0x00, 0x00, 0x00, 0x49,
        0x45, 0x4E, 0x44, 0xAE, 0x42, 0x60, 0x82
    ];

    [Fact]
    public void Wraps_string_image_url_as_openai_object()
    {
        var payload = MessagePayload(new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = "data:image/png;base64,abc"
        });

        Assert.Equal(1, LocalChatImageNormalizer.Normalize(payload));
        var part = ImagePart(payload);
        Assert.Equal("image_url", part["type"]?.ToString());
        Assert.Equal("data:image/png;base64,abc", part["image_url"]?["url"]?.ToString());
    }

    [Fact]
    public void Converts_anthropic_base64_image_to_data_uri()
    {
        var payload = MessagePayload(new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject
            {
                ["type"] = "base64",
                ["media_type"] = "image/png",
                ["data"] = "abc123"
            }
        });

        LocalChatImageNormalizer.Normalize(payload);
        Assert.Equal("data:image/png;base64,abc123", ImagePart(payload)["image_url"]?["url"]?.ToString());
    }

    [Fact]
    public void Reads_local_file_path_into_data_uri()
    {
        var path = Path.Combine(Path.GetTempPath(), "fluxmux-image-test.png");
        File.WriteAllBytes(path, OnePixelPng);
        try
        {
            var payload = MessagePayload(new JsonObject
            {
                ["type"] = "image_url",
                ["image_url"] = new JsonObject { ["url"] = path }
            });

            LocalChatImageNormalizer.Normalize(payload);
            var url = ImagePart(payload)["image_url"]?["url"]?.ToString() ?? string.Empty;
            Assert.StartsWith("data:image/png;base64,", url, StringComparison.OrdinalIgnoreCase);
            var raw = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
            Assert.Equal(OnePixelPng, raw);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Fetches_http_image_url_into_data_uri()
    {
        const string remote = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTestThumb&s=10";
        using var http = new HttpClient(new StaticImageHandler(OnePixelPng, "image/png"));
        var payload = MessagePayload(new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = remote }
        });

        Assert.Equal(1, await LocalChatImageNormalizer.NormalizeAsync(payload, http));
        var url = ImagePart(payload)["image_url"]?["url"]?.ToString() ?? string.Empty;
        Assert.StartsWith("data:image/png;base64,", url, StringComparison.OrdinalIgnoreCase);
        var raw = Convert.FromBase64String(url["data:image/png;base64,".Length..]);
        Assert.Equal(OnePixelPng, raw);
    }

    [Fact]
    public async Task Promotes_markdown_image_url_and_embeds_it()
    {
        const string remote = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTestThumb&s=10";
        using var http = new HttpClient(new StaticImageHandler(OnePixelPng, "image/jpeg"));
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "look at ![thumb](" + remote + ")"
                }
            }
        };

        Assert.Equal(1, await LocalChatImageNormalizer.NormalizeAsync(payload, http));
        var parts = (JsonArray)payload["messages"]![0]!["content"]!;
        Assert.Equal(2, parts.Count);
        var url = parts[1]!["image_url"]?["url"]?.ToString() ?? string.Empty;
        Assert.StartsWith("data:image/png;base64,", url, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Does_not_embed_markdown_pictures_from_system_or_tool_text()
    {
        const string remote = "https://encrypted-tbn0.gstatic.com/images?q=tbn:ANd9GcTestThumb&s=10";
        using var http = new HttpClient(new StaticImageHandler(OnePixelPng, "image/jpeg"));
        var payload = new JsonObject
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "system",
                    ["content"] = "Docs ![a](" + remote + ") ![b](" + remote + "?x=1) ![c](" + remote + "?x=2)"
                },
                new JsonObject
                {
                    ["role"] = "tool",
                    ["content"] = "see ![d](" + remote + "?x=3)"
                },
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = "no picture, just text"
                }
            }
        };

        Assert.Equal(0, await LocalChatImageNormalizer.NormalizeAsync(payload, http));
        Assert.False(LocalChatPayloadSignals.PayloadHasImage(payload));
    }

    private static JsonObject MessagePayload(JsonObject part)
        => new()
        {
            ["messages"] = new JsonArray
            {
                new JsonObject
                {
                    ["role"] = "user",
                    ["content"] = new JsonArray { part }
                }
            }
        };

    private static JsonObject ImagePart(JsonObject payload)
        => (JsonObject)payload["messages"]![0]!["content"]![0]!;

    private sealed class StaticImageHandler : HttpMessageHandler
    {
        private readonly byte[] _bytes;
        private readonly string _contentType;

        public StaticImageHandler(byte[] bytes, string contentType)
        {
            _bytes = bytes;
            _contentType = contentType;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_bytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue(_contentType);
            return Task.FromResult(response);
        }
    }
}
