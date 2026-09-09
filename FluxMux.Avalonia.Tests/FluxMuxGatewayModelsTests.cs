using System.Linq;
using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

public sealed class FluxMuxGatewayModelsTests
{
    [Fact]
    public void Local_ready_lists_local_with_loaded_context_not_train_size_or_cline_default()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "ready",
            ["local_preferred_model"] = "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            ["local_context"] = 163840
        });

        var data = listed["data"] as JsonArray;
        Assert.NotNull(data);
        var local = Find(data, "local");
        Assert.NotNull(local);
        Assert.Equal(163840, local["context_length"]!.GetValue<int>());
        Assert.Equal(163840, local["max_model_len"]!.GetValue<int>());
        Assert.Equal(163840, local["meta"]!["n_ctx"]!.GetValue<int>());
        Assert.Null(local["meta"]!["n_ctx_train"]);
        Assert.NotEqual(128000, local["context_length"]!.GetValue<int>());
        Assert.NotEqual(262144, local["context_length"]!.GetValue<int>());
        Assert.Equal("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", local["name"]!.GetValue<string>());

        var file = Find(data, "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf");
        Assert.NotNull(file);
        Assert.Equal(163840, file["context_length"]!.GetValue<int>());
    }

    [Fact]
    public void Local_ready_lists_the_Cline_profile_label_including_variant()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "ready",
            ["local_preferred_model"] = "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            ["local_variant"] = "max context",
            ["local_context"] = 213056
        });

        var data = listed["data"] as JsonArray;
        Assert.NotNull(data);
        var labeled = Find(data, "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)");
        Assert.NotNull(labeled);
        Assert.Equal(213056, labeled["context_length"]!.GetValue<int>());
        Assert.Equal(
            "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)",
            FluxMuxGatewayModels.FormatProfileId("Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf", "max context"));
    }

    [Fact]
    public void Cloud_ready_does_not_put_cloud_context_on_local_when_llama_server_is_loaded()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "ready",
            ["cloud_phase"] = "ready",
            ["local_preferred_model"] = "local.gguf",
            ["cloud_preferred_model"] = "stub-cloud",
            ["local_context"] = 213056,
            ["context_window"] = 128000
        });

        var data = listed["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.Equal(213056, Find(data, "local")!["context_length"]!.GetValue<int>());
        Assert.Equal(128000, Find(data, "stub-cloud")!["context_length"]!.GetValue<int>());
    }

    [Fact]
    public void Cloud_only_still_lists_local_and_the_Cline_profile_label()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "idle",
            ["cloud_phase"] = "ready",
            ["local_preferred_model"] = "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            ["local_variant"] = "max context",
            ["local_context"] = 213056,
            ["cloud_preferred_model"] = "gemini-3-flash-preview",
            ["context_window"] = 8192
        });

        var data = listed["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.NotNull(Find(data, "local"));
        Assert.NotNull(Find(data, "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)"));
        Assert.Equal(213056, Find(data, "local")!["context_length"]!.GetValue<int>());
        Assert.Equal(8192, Find(data, "gemini-3-flash-preview")!["context_length"]!.GetValue<int>());
    }

    [Fact]
    public void Empty_state_still_lists_local_so_Cline_can_open()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject());
        var data = listed["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.NotNull(Find(data, "local"));
    }

    [Fact]
    public void Cloud_only_falls_back_to_last_local_profile_label()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "idle",
            ["cloud_phase"] = "ready",
            ["local_preferred_model"] = "",
            ["last_local_preferred_model"] = "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            ["last_local_variant"] = "max context",
            ["cloud_preferred_model"] = "gemini-3-flash-preview",
            ["context_window"] = 8192
        });

        var data = listed["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.NotNull(Find(data, "local"));
        Assert.NotNull(Find(data, "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)"));
        Assert.NotNull(Find(data, "gemini-3-flash-preview"));
    }

    [Fact]
    public void Extra_ids_keep_Cline_stored_profile_label_in_the_catalog()
    {
        var listed = FluxMuxGatewayModels.List(
            new JsonObject
            {
                ["local_preferred_model"] = "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
                ["local_variant"] = "(defaults)",
                ["local_context"] = 213056
            },
            ["Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)", "local"]);

        var data = listed["data"] as JsonArray;
        Assert.NotNull(data);
        Assert.NotNull(Find(data, "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf (max context)"));
        Assert.NotNull(Find(data, "local"));
    }

    [Fact]
    public void Local_ready_advertises_the_reply_budget_and_images_so_any_client_can_read_them()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "ready",
            ["local_preferred_model"] = "Qwen3.8-27B-NVFP4-MTP-Q8attn.gguf",
            ["local_context"] = 163840,
            ["local_max_tokens"] = 16384,
            ["local_vision"] = "Enabled"
        });

        var local = Find((listed["data"] as JsonArray)!, "local");
        Assert.NotNull(local);
        Assert.Equal(16384, local["max_tokens"]!.GetValue<int>());
        Assert.Equal(16384, local["max_output_tokens"]!.GetValue<int>());
        Assert.Equal(16384, local["meta"]!["n_predict"]!.GetValue<int>());
        Assert.True(local["supports_images"]!.GetValue<bool>());
        Assert.True(local["supports_vision"]!.GetValue<bool>());

        // Context is still there, since a client may read only that.
        Assert.Equal(163840, local["context_length"]!.GetValue<int>());
    }

    [Fact]
    public void Images_off_is_stated_rather_than_left_out()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "ready",
            ["local_preferred_model"] = "text-only.gguf",
            ["local_context"] = 213056,
            ["local_vision"] = "Disabled"
        });

        var local = Find((listed["data"] as JsonArray)!, "local");
        Assert.NotNull(local);
        Assert.False(local["supports_images"]!.GetValue<bool>());
        Assert.False(local["meta"]!["vision"]!.GetValue<bool>());
    }

    [Fact]
    public void An_unknown_capability_is_left_unsaid_rather_than_claimed_either_way()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "ready",
            ["local_preferred_model"] = "unknown.gguf",
            ["local_context"] = 131072
        });

        var local = Find((listed["data"] as JsonArray)!, "local");
        Assert.NotNull(local);
        Assert.Null(local["supports_images"]);
        Assert.Null(local["supports_vision"]);
        Assert.Null(local["max_tokens"]);
        Assert.Null(local["meta"]!["n_predict"]);
        Assert.Equal(131072, local["meta"]!["n_ctx"]!.GetValue<int>());
    }

    [Fact]
    public void Cloud_advertises_its_own_budget_and_images_not_the_local_ones()
    {
        var listed = FluxMuxGatewayModels.List(new JsonObject
        {
            ["local_phase"] = "ready",
            ["cloud_phase"] = "ready",
            ["local_preferred_model"] = "local.gguf",
            ["local_context"] = 213056,
            ["local_max_tokens"] = 16384,
            ["local_vision"] = "Disabled",
            ["cloud_preferred_model"] = "stub-cloud",
            ["context_window"] = 128000,
            ["max_tokens"] = 8192,
            ["cloud_vision"] = "Enabled"
        });

        var data = (listed["data"] as JsonArray)!;
        var cloud = Find(data, "stub-cloud");
        Assert.NotNull(cloud);
        Assert.Equal(8192, cloud["max_tokens"]!.GetValue<int>());
        Assert.True(cloud["supports_images"]!.GetValue<bool>());

        var local = Find(data, "local");
        Assert.Equal(16384, local!["max_tokens"]!.GetValue<int>());
        Assert.False(local["supports_images"]!.GetValue<bool>());
    }

    [Fact]
    public void Reading_the_reply_budget_back_off_the_launch_args_matches_what_llama_server_was_told()
    {
        string[] args = ["-m", "model.gguf", "-c", "163840", "-n", "16384", "--port", "5002"];
        Assert.Equal(16384, FluxMuxRuntimeService.ReadIntArg(args, "-n"));
        Assert.Equal(163840, FluxMuxRuntimeService.ReadIntArg(args, "-c"));
        Assert.Equal(0, FluxMuxRuntimeService.ReadIntArg(args, "--not-there"));
        Assert.Equal(0, FluxMuxRuntimeService.ReadIntArg(["-n"], "-n"));
    }

    private static JsonObject? Find(JsonArray data, string id)
        => data.OfType<JsonObject>().FirstOrDefault(entry =>
            (entry["id"]?.ToString() ?? string.Empty).Equals(id, System.StringComparison.OrdinalIgnoreCase));
}
