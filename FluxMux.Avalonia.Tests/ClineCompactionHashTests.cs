using System.Text.Json.Nodes;
using FluxMux.Avalonia.Services;
using Xunit;

namespace FluxMux.Avalonia.Tests;

/// <summary>
/// Cline applies a .compaction.json only when its source_prefix_hash equals Cline's own hash of
/// the task's first messages; otherwise it silently loads the whole chat. Cline 4.1.27 unwraps
/// &lt;user_input&gt; / &lt;user_command&gt; and drops &lt;mode_notice&gt; before hashing, and
/// AI-FluxMux only trimmed, so a live Hail Mary rebuild was ignored on reopen (26 Nov). The
/// expected hash was computed by Cline's own functions (PGo, JPp, RGo, s0 from its extension.js)
/// run in node on this same fixture.
/// </summary>
public sealed class ClineCompactionHashTests
{
    private const string Fixture = """
        [{"id":"u1","role":"user","content":[{"type":"text","text":"  <user_input mode=\"act\">Build the exporter – naïve “quotes” 🚀</user_input>  "}],"ts":1},
         {"id":"a1","role":"assistant","content":[{"type":"text","text":"Sure."},{"type":"tool_use","id":"t1","name":"run_commands","input":{"commands":["dir"]}}],"ts":2,"modelInfo":{"id":"local","provider":"openai-compatible"},"metrics":{"inputTokens":5}},
         {"id":"r1","role":"user","content":[{"type":"tool_result","tool_use_id":"t1","name":"run_commands","content":[{"query":"dir","result":"a\r\nb"}]}],"ts":3},
         {"id":"u2","role":"user","content":"<user_input mode=\"plan\">Plan it.</user_input><mode_notice>You are in plan mode.</mode_notice>","ts":4},
         {"id":"u3","role":"user","content":[{"type":"text","text":"<user_command slash=\"smol\">tidy up</user_command>"}],"ts":5},
         {"id":"u4","role":"user","content":[{"type":"text","text":"<user_input mode=\"act\">unclosed"}],"ts":6}]
        """;

    [Fact]
    public void The_prefix_hash_matches_Cline_4_1_27_for_wrapped_user_text()
    {
        var messages = JsonNode.Parse(Fixture)!.AsArray();
        Assert.Equal(
            "sha256:22fb8a72359d2c17096aa7f7541133c82cceaf439dd1d5ba375db1b596abeeb9",
            ClineCompactionWriter.ComputeClineSourcePrefixHash(messages, messages.Count));
    }

    [Theory]
    [InlineData("  <user_input mode=\"act\">Build it</user_input> ", "Build it")]
    [InlineData("<user_input mode=\"plan\">Plan it.</user_input><mode_notice>You are in plan mode.</mode_notice>", "Plan it.")]
    [InlineData("<user_command slash=\"smol\">tidy up</user_command>", "/smol tidy up")]
    [InlineData("<user_command slash=\"team\">Spawn a team of agents for the following task: port it</user_command>", "/team port it")]
    [InlineData("<user_input mode=\"act\">unclosed", "unclosed")]
    [InlineData("  plain text  ", "plain text")]
    public void User_text_is_normalised_as_Cline_does(string input, string expected)
        => Assert.Equal(expected, ClineCompactionWriter.ClineUserText(input));
}
